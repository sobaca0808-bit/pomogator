using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace PomogatorWifi
{
    internal static class ClientDiagnostics
    {
        private static readonly HashSet<string> DriverProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NDIS", "Microsoft-Windows-NDIS", "Microsoft-Windows-Kernel-PnP", "Microsoft-Windows-USB-USBHUB3",
            "Microsoft-Windows-DriverFrameworks-UserMode", "netr28u", "netr28ux", "netr28ux64", "rt2870", "rt2870sta", "Ralink"
        };

        internal static string Capture(TimeSpan duration, CancellationToken token)
        {
            var from = DateTimeOffset.UtcNow;
            var observation = new ClientObservation();
            var events = new ConcurrentQueue<HostedTraceEvent>();
            var count = 0;
            var lines = new List<string>();
            var adapterLine = "Виртуальный адаптер — не подтверждён.";
            string radioLine = null;
            string wlanError = null;
            var elapsed = Stopwatch.StartNew();
            HostedNetwork network = null;
            try
            {
                while (elapsed.Elapsed < duration)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (network == null)
                            network = new HostedNetwork(monitor: true, trace: item =>
                            {
                                if (Interlocked.Increment(ref count) <= 40) events.Enqueue(item);
                            });
                        if (radioLine == null)
                        {
                            try { radioLine = network.ReadRadio(); }
                            catch (WlanFailure error) { radioLine = error.Message; }
                        }
                        var state = network.ReadPeers(out var id, out var clients);
                        var ipv4 = InspectAdapter(id, clients, out adapterLine);
                        observation.Sample(state, id, clients.Length, ipv4);
                    }
                    catch (WlanFailure error)
                    {
                        wlanError = error.Message;
                        network?.Dispose();
                        network = null;
                    }
                    while (events.TryDequeue(out var activity))
                    {
                        observation.Observe(activity);
                        lines.Add(activity.Line());
                    }
                    if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) token.ThrowIfCancellationRequested();
                }
            }
            finally { network?.Dispose(); }

            while (events.TryDequeue(out var activity))
            {
                observation.Observe(activity);
                lines.Add(activity.Line());
            }
            var until = DateTimeOffset.UtcNow;
            var verdict = new List<string> { $"Попытка подключения · {from.ToLocalTime():HH:mm:ss}–{until.ToLocalTime():HH:mm:ss}" };
            verdict.AddRange(observation.Lines());
            verdict.Add(adapterLine);
            if (radioLine != null) verdict.Add(radioLine);
            if (wlanError != null) verdict.Add("Последняя ошибка WLAN за время записи · " + wlanError);
            verdict.AddRange(lines);
            if (count > 40) verdict.Add("Показаны первые 40 уведомлений Hosted Network.");
            verdict.AddRange(ReadEvents("Microsoft-Windows-WLAN-AutoConfig/Operational", from, until, false, token));
            verdict.AddRange(ReadEvents("System", from, until, true, token));
            return string.Join(Environment.NewLine, verdict);
        }

        private static ClientIpv4 InspectAdapter(Guid id, ulong[] clients, out string line)
        {
            line = "Виртуальный адаптер — Windows не показала его по GUID Hosted Network.";
            if (id == Guid.Empty) return ClientIpv4.Unknown;
            try
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
                    Guid.TryParse(item.Id, out var actual) && actual == id);
                if (adapter == null) return ClientIpv4.Unknown;
                var properties = adapter.GetIPProperties();
                var addresses = properties.UnicastAddresses.Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ip => ip.Address.GetAddressBytes()).ToArray();
                var regular = addresses.Any(address => address[0] != 0 && !(address[0] == 169 && address[1] == 254));
                line = $"Виртуальный адаптер — {adapter.OperationalStatus}, IPv4: " +
                    (regular ? "есть обычный адрес" : addresses.Length > 0 ? "есть только локальный или нулевой адрес" : "адрес отсутствует");
                var index = properties.GetIPv4Properties()?.Index ?? 0;
                return index > 0 && clients.Length > 0 ? ReadNeighbors(index, clients) : ClientIpv4.Unknown;
            }
            catch (Exception error) when (error is NetworkInformationException || error is SocketException)
            {
                line = "Виртуальный адаптер — ошибка чтения Windows 0x" + error.HResult.ToString("X8");
                return ClientIpv4.Unknown;
            }
        }

        private static ClientIpv4 ReadNeighbors(int index, ulong[] clients)
        {
            if (GetIpNetTable2(2, out var table) != 0 || table == IntPtr.Zero) return ClientIpv4.Unknown;
            try { return ClientObservation.ReadIpv4Table(table, index, clients); }
            finally { FreeMibTable(table); }
        }

        private static IEnumerable<string> ReadEvents(string channel, DateTimeOffset from, DateTimeOffset until, bool system,
            CancellationToken token)
        {
            var since = from.AddSeconds(-5).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var end = until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var source = system ? "System · драйверы, NDIS и USB (могут относиться к другим устройствам)" :
                "WLAN AutoConfig · события Windows, включая работу ПК как клиента";
            var lines = new List<string> { source };
            var filter = system ? " and (Level=1 or Level=2 or Level=3)" : "";
            var xpath = $"*[System[TimeCreated[@SystemTime >= '{since}' and @SystemTime <= '{end}']{filter}]]";
            var time = Stopwatch.StartNew();
            try
            {
                using (var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true }))
                    for (var i = 0; i < 120 && lines.Count <= 18 && time.Elapsed < TimeSpan.FromSeconds(2); i++)
                    {
                        token.ThrowIfCancellationRequested();
                        using (var item = reader.ReadEvent(TimeSpan.FromMilliseconds(250)))
                        {
                            if (item == null) break;
                            if (system && !DriverProviders.Contains(item.ProviderName)) continue;
                            if (!system && item.ProviderName != "Microsoft-Windows-WLAN-AutoConfig") continue;
                            // Event payloads and descriptions may contain network names and addresses.
                            lines.Add($"{item.TimeCreated:HH:mm:ss} · {item.ProviderName} · ID {item.Id} · уровень {item.Level}");
                        }
                    }
                if (lines.Count == 1) lines.Add("В окне наблюдения подходящие события не найдены.");
                else lines.Reverse(1, lines.Count - 1);
            }
            catch (Exception error) when (error is EventLogException || error is UnauthorizedAccessException || error is SecurityException)
            {
                lines.Add("Журнал недоступен или чтение прервано. Код 0x" + error.HResult.ToString("X8") + ".");
            }
            return lines;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIpNetTable2(ushort family, out IntPtr table);
        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr buffer);
    }
}
