using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PomogatorWifi
{
    internal enum SharingState { Unknown, Disabled, Verified, Other }

    internal sealed class WifiReport
    {
        internal WifiPhase Phase;
        internal bool? HasWifi, Pppoe;
        internal MobilePhase Mobile;
        internal Guid Owner, Adapter;
        internal string Ssid, PairDescription;
        internal uint? Peers;
        internal SharingState Sharing;
        internal SharingLease Candidate, Lease;
        internal string Mechanism, SharingNote;
        internal bool CanRepair;

        internal SharingLease OwnedLease => Lease != null && Owner != Guid.Empty && Lease.Owner == Owner ? Lease : null;
    }

    internal static class NetworkReading
    {
        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static readonly HttpClient Web = new HttpClient(new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(5) };

        internal static Task<T> OnSta<T>(Func<T> run)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { result.SetResult(run()); }
                catch (Exception error) { result.SetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return result.Task;
        }

        internal static async Task<bool> Internet()
        {
            try
            {
                using (var response = await Web.GetAsync("https://www.microsoft.com/",
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    return response.IsSuccessStatusCode;
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
            {
                return false;
            }
        }

        private static void Once(string stage, string safeDetails)
        {
            lock (Reported)
                if (Reported.Add(stage + safeDetails)) Local.Log(stage, safeDetails);
        }

        internal static IcsSnapshot OpenIcs()
        {
            var snapshot = new IcsSnapshot();
            foreach (var issue in snapshot.Issues)
                Once(issue.Unsupported ? "ics_unsupported_object" : "ics_unverified_object", issue.Detail);
            return snapshot;
        }

        internal static IcsSnapshot TryIcs()
        {
            try { return OpenIcs(); }
            catch (IcsFailure error)
            {
                Once("ics_read_failed", error.Operation + string.Format(" HRESULT=0x{0:X8}", error.HResult));
                return null;
            }
        }

        internal static bool WifiDirect(IcsSnapshot snapshot) =>
            snapshot.Connections.Any(connection => connection.Connected && (connection.Flags & Characteristics.WifiDirect) != 0 &&
                connection.Sharing == SharingRole.Private);

        internal static WifiReport Read()
        {
            var report = new WifiReport { Mobile = MobileHotspot.Read() };
            var hosted = HostedNetwork.State.Unavailable;
            try
            {
                using (var wlan = new HostedNetwork())
                {
                    report.HasWifi = wlan.HasWifi();
                    hosted = wlan.Query(out report.Adapter, out var peers);
                    if (hosted == HostedNetwork.State.Active)
                    {
                        report.Peers = peers;
                        report.Ssid = wlan.Ssid();
                    }
                }
            }
            catch (Exception error) { Once("wlan_read_failed", error is WifiError ? error.Message : error.GetType().Name); }

            var ownsActive = false;
            try
            {
                var owner = Owner.Request(new OwnerRequest { Command = "status" }, 180);
                if (owner != null)
                {
                    report.Owner = owner.Owner;
                    ownsActive = owner.Owner != Guid.Empty && owner.Active && owner.Adapter == report.Adapter;
                }
            }
            catch (Exception error) { Once("owner_read_failed", error.GetType().Name); }
            try { report.Lease = Local.Load<SharingLease>(Local.LeasePath); }
            catch (Exception error)
            {
                Once("ics_lease_read_failed", error.GetType().Name);
                report.SharingNote = "Не удалось открыть сохранённое разрешение ICS. Изменение недоступно.";
            }

            using (var snapshot = TryIcs())
            {
                bool wifiDirect = snapshot != null && WifiDirect(snapshot);
                report.Phase = WifiRules.Phase(report.HasWifi.HasValue, hosted, ownsActive, report.Mobile, wifiDirect);
                report.Mechanism = hosted == HostedNetwork.State.Active ? "Hosted Network" :
                    report.Phase == WifiPhase.External ? "Мобильный хот-спот / Wi-Fi Direct" :
                    hosted == HostedNetwork.State.Idle ? "Hosted Network доступна" :
                    report.HasWifi == false ? "Wi-Fi-адаптер не найден" : "Hosted Network недоступна";
                if (snapshot != null) ReadSharing(report, snapshot, wifiDirect);
            }
            return report;
        }

        private static void ReadSharing(WifiReport report, IcsSnapshot snapshot, bool wifiDirect)
        {
            var connections = snapshot.Connections;
            var complete = !snapshot.Issues.Exists(issue => !issue.Unsupported || issue.Id == report.Adapter);
            var tracked = report.OwnedLease;
            var source = tracked == null ? null : connections.SingleOrDefault(c => c.Id == tracked.Source && c.IsWanSharingMedia);
            var pppoe = connections.Where(c => c.Connected && (c.IsPppoe || c == source ||
                c.Media == MediaType.SharedAccessHostRas && report.Lease?.Source == c.Id)).ToArray();
            var rasUnknown = connections.Any(c => c.Media == MediaType.SharedAccessHostRas && c.Connected);
            report.Pppoe = pppoe.Length > 0 ? true : complete && !rasUnknown ? (bool?)false : null;
            if (tracked != null)
                report.Pppoe = source?.Connected ?? (complete ? (bool?)false : null);
            if (!complete)
            {
                report.SharingNote = "Изменение ICS недоступно: Windows не вернула роли всех сетевых подключений.";
                return;
            }

            var roles = connections.Where(connection => connection.Sharing.HasValue).ToArray();
            var target = wifiDirect && report.Phase == WifiPhase.External && !report.Peers.HasValue
                ? roles.SingleOrDefaultSafe(c => c.Sharing == SharingRole.Private && (c.Flags & Characteristics.WifiDirect) != 0)?.Id ?? Guid.Empty
                : report.Phase == WifiPhase.Owned || report.Peers.HasValue ? report.Adapter : Guid.Empty;
            if (roles.Length == 2 && target != Guid.Empty &&
                roles.Any(c => c.Id != target && c.Connected && c.Sharing == SharingRole.Public) &&
                roles.Any(c => c.Id == target && c.IsLan && c.Connected && c.Sharing == SharingRole.Private))
                report.Sharing = SharingState.Verified;
            else
                report.Sharing = roles.Length == 0 ? SharingState.Disabled : SharingState.Other;

            if (report.Phase != WifiPhase.Owned || target == Guid.Empty) return;
            var destination = Policy.FindTarget(connections, target);
            if (tracked != null && tracked.Target == target && source != null && destination != null)
                report.PairDescription = DescribePair(source, destination);
            if (tracked != null && tracked.Target == target && source != null && source.Connected &&
                destination != null &&
                Policy.CanRestore(connections, tracked.Source, tracked.Target, true, true))
                report.CanRepair = !Policy.ConfirmsSharing(connections, tracked.Source, tracked.Target);

            if (report.SharingNote != null || report.CanRepair) return;
            if (report.Sharing == SharingState.Other)
                report.SharingNote = "Общий доступ уже настроен для другой пары или неполностью. Проверьте вкладку «Доступ» в ncpa.cpl.";
            if (report.Sharing != SharingState.Disabled || tracked != null) return;
            if (destination == null || pppoe.Length != 1 || !pppoe[0].IsPppoe)
            {
                report.SharingNote = "Для настройки ICS нужны одно активное PPPoE и готовый виртуальный адаптер.";
                return;
            }
            report.Candidate = new SharingLease { Owner = report.Owner, Source = pppoe[0].Id, Target = target };
            report.PairDescription = DescribePair(pppoe[0], destination);
        }

        private static string DescribePair(Connection source, Connection destination) =>
            "Источник интернета: " + source.Name + "\r\nАдаптер точки доступа: " + destination.Name;

        private static T SingleOrDefaultSafe<T>(this IEnumerable<T> items, Func<T, bool> matches) where T : class
        {
            var candidates = items.Where(matches).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
}
