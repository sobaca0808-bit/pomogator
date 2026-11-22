using System;
using System.Linq;
using System.Threading;

namespace PomogatorWifi
{
    internal enum SharingResult { Declined, Existing, Verified }
    internal delegate HostedNetwork.State QueryHosted(out Guid adapter);

    internal sealed class Session
    {
        private readonly Func<IcsSnapshot> openSnapshot;
        private readonly QueryHosted query;
        private readonly Action pause;
        internal Guid Source { get; }
        internal Guid Target { get; }
        private readonly string sourceName;
        private readonly string targetName;
        private bool publicAttempted;
        private bool privateAttempted;
        internal bool OwnsChanges => publicAttempted || privateAttempted;
        internal string Pair => "Интернет: " + sourceName + "\r\nАдаптер Wi-Fi: " + targetName;

        private Session(QueryHosted query, Func<IcsSnapshot> openSnapshot, Action pause, Connection source, Connection target)
        {
            this.query = query;
            this.openSnapshot = openSnapshot;
            this.pause = pause;
            Source = source.Id;
            Target = target.Id;
            sourceName = source.Name;
            targetName = target.Name;
        }

        internal static Session Find(QueryHosted query, Func<IcsSnapshot> openSnapshot, Action pause = null)
        {
            if (pause == null) pause = () => Thread.Sleep(250);
            for (int attempt = 0; attempt < 24; attempt++)
            {
                Guid targetId;
                if (query(out targetId) != HostedNetwork.State.Active)
                    throw new WifiError("Точка доступа остановилась. Проверьте, подключён ли Ralink.");
                if (targetId != Guid.Empty)
                {
                    using (var snapshot = openSnapshot())
                    {
                        snapshot.RequireVerified(Guid.Empty, targetId);
                        var source = Policy.ChooseSource(snapshot.Connections);
                        var target = Policy.FindTarget(snapshot.Connections, targetId);
                        if (target != null) return new Session(query, openSnapshot, pause, source, target);
                    }
                }
                pause();
            }
            throw new WifiError("Windows не показала активный виртуальный адаптер Hosted Network. Проверьте диспетчер устройств и драйвер Ralink.");
        }

        internal SharingResult ConfigureSharing(Func<string, bool> confirm, Action beforeWrite = null, bool repair = false)
        {
            RequireActiveTarget();
            using (var snapshot = openSnapshot())
            {
                if (!NeedsSharing(snapshot, repair)) return SharingResult.Existing;
            }

            if (!confirm(Pair)) return SharingResult.Declined;
            RequireActiveTarget();
            using (var snapshot = openSnapshot())
            {
                if (!NeedsSharing(snapshot, repair)) return SharingResult.Existing;
                beforeWrite?.Invoke();
                publicAttempted = true;
                privateAttempted = true; // Windows can assign the private role when public sharing starts.
                if (!snapshot.Connections.Any(connection => connection.Id == Source && connection.Sharing == SharingRole.Public))
                    snapshot.Enable(Source, SharingRole.Public);
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                RequireActiveTarget();
                using (var snapshot = openSnapshot())
                {
                    snapshot.RequireVerified(Source, Target);
                    var target = Policy.FindTarget(snapshot.Connections, Target);
                    var upstream = snapshot.Connections.SingleOrDefault(c => c.Id == Source);
                    if (upstream == null || !upstream.Connected || !upstream.IsWanSharingMedia || target == null ||
                        !Policy.CanRestore(snapshot.Connections, Source, Target, true, true))
                        throw new WifiError("Подключения изменились при включении общего доступа. Проверьте вкладку «Доступ» в ncpa.cpl.");
                    if (upstream.Sharing == SharingRole.Public)
                    {
                        if (target.Sharing == SharingRole.Private && snapshot.IsSharing(Source, Target))
                            return SharingResult.Verified;
                        if (!target.Sharing.HasValue) snapshot.Enable(Target, SharingRole.Private);
                        break;
                    }
                }
                pause();
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                RequireActiveTarget();
                using (var snapshot = openSnapshot())
                    if (snapshot.IsSharing(Source, Target)) return SharingResult.Verified;
                pause();
            }
            throw new WifiError("Windows не подтвердила роли ICS для PPPoE и виртуального адаптера. Проверьте вкладку «Доступ» в ncpa.cpl.");
        }

        private bool NeedsSharing(IcsSnapshot snapshot, bool repair)
        {
            snapshot.RequireVerified(Source, Target);
            if (snapshot.IsSharing(Source, Target)) return false;
            if (!repair) return Policy.NeedsSharing(snapshot.Connections, Source, Target);
            var source = snapshot.Connections.SingleOrDefault(c => c.Id == Source && c.Connected && c.IsWanSharingMedia);
            if (source == null || Policy.FindTarget(snapshot.Connections, Target) == null ||
                !Policy.CanRestore(snapshot.Connections, Source, Target, true, true))
                throw new WifiError("Исходная пара ICS изменилась. Проверьте подключения.");
            return true;
        }

        internal static Session ForPair(QueryHosted query, Func<IcsSnapshot> open, Guid source, Guid target)
        {
            using (var snapshot = open())
            {
                snapshot.RequireVerified(source, target);
                var a = snapshot.Connections.SingleOrDefault(c => c.Id == source);
                var b = snapshot.Connections.SingleOrDefault(c => c.Id == target);
                if (a == null || b == null || !a.IsWanSharingMedia || !b.IsLan)
                    throw new WifiError("Windows не подтвердила прежнюю пару подключения.");
                return new Session(query, open, () => Thread.Sleep(250), a, b);
            }
        }

        internal void RestoreOwnedPair()
        {
            RequireActiveTarget();
            publicAttempted = privateAttempted = true;
            Restore();
        }

        private void RequireActiveTarget()
        {
            Guid adapter;
            if (query(out adapter) != HostedNetwork.State.Active || adapter != Target)
                throw new WifiError("Точка доступа остановилась или виртуальный адаптер изменился. Запустите точку снова.");
        }

        internal static string SetupFailureMessage(Exception error)
        {
            var detail = error is WifiError || error is IcsFailure
                ? error.Message : string.Format("Ошибка Windows (0x{0:X8}).", error.GetBaseException().HResult);
            return "Настройка ICS не завершена. " + detail;
        }

        internal void Restore()
        {
            if (privateAttempted) Disable(Target, SharingRole.Private);
            privateAttempted = false;
            if (publicAttempted) Disable(Source, SharingRole.Public);
            publicAttempted = false;
        }

        private void Disable(Guid id, SharingRole role)
        {
            using (var snapshot = openSnapshot())
            {
                snapshot.RequireVerified(Source, Target);
                var current = snapshot.Connections.SingleOrDefault(c => c.Id == id);
                if (current == null || !current.Sharing.HasValue) return;
                if (!Policy.CanRestore(snapshot.Connections, Source, Target, publicAttempted, privateAttempted))
                    throw new WifiError("Общий доступ изменён в Windows. Проверьте текущую пару в ncpa.cpl.");
                if (current.Sharing == role) snapshot.Disable(id);
            }
        }
    }
}
