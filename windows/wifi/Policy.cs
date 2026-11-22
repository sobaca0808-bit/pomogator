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

    // Values from Windows SDK netcon.h.
    internal enum MediaType { None = 0, Lan = 3, Pppoe = 6, SharedAccessHostLan = 8, SharedAccessHostRas = 9 }
    internal enum ConnectionStatus { Disconnected = 0, Connected = 2, HardwareDisabled = 5 }

    [Flags]
    internal enum Characteristics : uint
    {
        Shared = 0x100, HomeNetCapable = 0x1000, SharedPrivate = 0x2000, HostedNetwork = 0x10000, WifiDirect = 0x40000
    }

    internal class Connection
    {
        internal Guid Id;
        internal string Name;
        internal MediaType Media;
        internal ConnectionStatus Status;
        internal Characteristics Flags;
        internal bool IsPppoe => Media == MediaType.Pppoe;
        internal bool Connected => Status == ConnectionStatus.Connected;
        internal bool IsWanSharingMedia => IsPppoe || Media == MediaType.SharedAccessHostRas;
        internal bool IsLan => Media == MediaType.Lan || Media == MediaType.SharedAccessHostLan;
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

        internal static Connection FindTarget(IEnumerable<Connection> connections, Guid hostedAdapter)
        {
            if (hostedAdapter == Guid.Empty) return null;
            return connections.SingleOrDefault(c => c.Id == hostedAdapter && c.Connected && c.IsLan);
        }

        // The source GUID was selected as PPPoE before enabling sharing; Windows may report a shared WAN afterward.
        internal static bool ConfirmsSharing(IList<Connection> connections, Guid source, Guid target)
        {
            return source != target && connections.Count(c => c.Sharing.HasValue) == 2 &&
                connections.Any(c => c.Id == source && c.Connected && c.IsWanSharingMedia && c.Sharing == SharingRole.Public) &&
                FindTarget(connections, target)?.Sharing == SharingRole.Private;
        }

        // False means the existing pair already has the exact requested sharing roles.
        internal static bool NeedsSharing(IList<Connection> connections, Guid source, Guid target)
        {
            if (source == Guid.Empty || target == Guid.Empty || source == target)
                throw new WifiError("Не удалось однозначно определить пару подключений для раздачи.");
            var actualSource = ChooseSource(connections);
            var actualTarget = FindTarget(connections, target);
            if (actualSource.Id != source || actualTarget == null)
                throw new WifiError("Подключения изменились или виртуальный адаптер ещё не готов. Выключите точку и попробуйте снова.");
            var shared = connections.Where(c => c.Sharing.HasValue).ToArray();
            if (shared.Length == 0) return true;
            if (shared.Length == 2 && actualSource.Sharing == SharingRole.Public &&
                actualTarget.Sharing == SharingRole.Private) return false;
            throw new WifiError("Общий доступ уже настроен для другой пары или настроен частично. Проверьте вкладку «Доступ» в ncpa.cpl. Утилита оставила прежнюю настройку.");
        }
    }
}
