using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PomogatorWifi
{
    internal sealed class WifiError : Exception
    {
        internal WifiError(string message) : base(message) { }
    }

    internal enum SharingRole { Public = 0, Private = 1 }

    internal class Connection
    {
        internal Guid Id;
        internal string Name;
        internal bool IsPppoe;
        internal bool Connected;
        internal SharingRole? Sharing;
    }

    internal static class Policy
    {
        internal static byte[] EncodeSsid(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || bytes.Length > 32)
                throw new WifiError("Имя Wi-Fi должно содержать от 1 до 32 байт UTF-8 без управляющих символов. Для кириллицы обычно помещается до 16 букв.");
            return bytes;
        }

        internal static void CheckPassword(string value)
        {
            if (value.Length < 8 || value.Length > 63 || value.Any(ch => ch < ' ' || ch > '~'))
                throw new WifiError("Пароль должен содержать 8–63 символа. Используйте латинские буквы, цифры и знаки ASCII.");
        }

        internal static Connection ChooseSource(IEnumerable<Connection> connections)
        {
            var candidates = connections.Where(c => c.IsPppoe && c.Connected).ToArray();
            if (candidates.Length == 0)
                throw new WifiError("Активное PPPoE не найдено. Подключите «Высокоскоростное подключение» в Windows и повторите проверку.");
            if (candidates.Length > 1)
                throw new WifiError("Активно несколько PPPoE-подключений. Определите нужное подключение в ncpa.cpl. Утилита не станет выбирать его автоматически.");
            return candidates[0];
        }

        internal static bool CanRestore(IEnumerable<Connection> connections, Guid source, Guid target,
            bool ownsPublic, bool ownsPrivate)
        {
            return connections.Where(c => c.Sharing.HasValue).All(c =>
                (ownsPublic && c.Id == source && c.Sharing == SharingRole.Public) ||
                (ownsPrivate && c.Id == target && c.Sharing == SharingRole.Private));
        }

        // False means the existing pair already has the exact requested sharing roles.
        internal static bool NeedsSharing(IList<Connection> connections, Guid source, Guid target)
        {
            if (source == Guid.Empty || target == Guid.Empty || source == target)
                throw new WifiError("Не удалось однозначно определить пару подключений для раздачи.");
            var actualSource = ChooseSource(connections);
            var actualTarget = connections.SingleOrDefault(c => c.Id == target);
            if (actualSource.Id != source || actualTarget == null || !actualTarget.Connected || actualTarget.IsPppoe)
                throw new WifiError("Подключения изменились или виртуальный адаптер ещё не готов. Выключите точку и попробуйте снова.");
            var shared = connections.Where(c => c.Sharing.HasValue).ToArray();
            if (shared.Length == 0) return true;
            if (shared.Length == 2 && actualSource.Sharing == SharingRole.Public &&
                actualTarget.Sharing == SharingRole.Private) return false;
            throw new WifiError("Общий доступ уже настроен для другой пары или настроен частично. Проверьте вкладку «Доступ» в ncpa.cpl. Утилита оставила прежнюю настройку.");
        }
    }
}
