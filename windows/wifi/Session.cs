using System;
using System.Linq;
using System.Threading;

namespace PomogatorWifi
{
    internal sealed class Session
    {
        internal Guid Source { get; private set; }
        internal Guid Target { get; private set; }
        internal string SourceName { get; private set; }
        internal string TargetName { get; private set; }
        private bool publicAttempted;
        private bool privateAttempted;
        internal bool OwnsChanges { get { return publicAttempted || privateAttempted; } }
        internal string Pair { get { return "Интернет: " + SourceName + "\r\nАдаптер Wi-Fi: " + TargetName; } }

        internal static Session Find(HostedNetwork wlan, Connection source)
        {
            // The virtual interface may first appear after Hosted Network becomes active.
            for (int attempt = 0; attempt < 24; attempt++)
            {
                Guid targetId;
                if (wlan.Query(out targetId) != HostedNetwork.State.Active)
                    throw new WifiError("Точка доступа остановилась. Проверьте, подключён ли Ralink.");
                if (targetId != Guid.Empty)
                {
                    using (var snapshot = new IcsSnapshot())
                    {
                        var target = snapshot.Connections.SingleOrDefault(c => c.Id == targetId);
                        if (target != null && target.Connected)
                            return new Session { Source = source.Id, SourceName = source.Name,
                                Target = targetId, TargetName = target.Name };
                    }
                }
                Thread.Sleep(250);
            }
            throw new WifiError("Windows не показала активный виртуальный адаптер Hosted Network. Проверьте диспетчер устройств и драйвер Ralink.");
        }

        internal bool NeedsSharing()
        {
            using (var snapshot = new IcsSnapshot())
                return Policy.NeedsSharing(snapshot.Connections, Source, Target);
        }

        internal void Enable(HostedNetwork wlan)
        {
            Guid target;
            if (wlan.Query(out target) != HostedNetwork.State.Active || target != Target)
                throw new WifiError("Точка доступа изменилась после подтверждения. Запустите её снова.");
            using (var snapshot = new IcsSnapshot())
            {
                if (!Policy.NeedsSharing(snapshot.Connections, Source, Target)) return;
                publicAttempted = true;
                privateAttempted = true;
                snapshot.Enable(Source, SharingRole.Public);
            }
            using (var snapshot = new IcsSnapshot())
            {
                // Some Windows versions can configure the private role automatically.
                var existing = snapshot.Connections.SingleOrDefault(c => c.Id == Target);
                var upstream = snapshot.Connections.SingleOrDefault(c => c.Id == Source);
                if (upstream == null || !upstream.Connected || upstream.Sharing != SharingRole.Public ||
                    existing == null || !existing.Connected ||
                    !Policy.CanRestore(snapshot.Connections, Source, Target, true, true))
                    throw new WifiError("Список подключений изменился при включении общего доступа. Проверьте вкладку «Доступ» в ncpa.cpl.");
                if (!existing.Sharing.HasValue) snapshot.Enable(Target, SharingRole.Private);
            }
            using (var snapshot = new IcsSnapshot())
                if (!snapshot.IsSharing(Source, Target))
                    throw new WifiError("Windows не подтвердила настройку раздачи. Проверьте службу ICS и вкладку «Доступ» в ncpa.cpl.");
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
            using (var snapshot = new IcsSnapshot())
            {
                var current = snapshot.Connections.SingleOrDefault(c => c.Id == id);
                if (current == null || !current.Sharing.HasValue) return;
                if (!Policy.CanRestore(snapshot.Connections, Source, Target, publicAttempted, privateAttempted))
                    throw new WifiError("Общий доступ изменён в Windows. Проверьте текущую пару в ncpa.cpl. Утилита оставила её настройки.");
                if (current.Sharing == role) snapshot.Disable(id);
            }
        }
    }
}
