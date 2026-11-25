using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace Pomogator.Vpn;

internal readonly record struct TunDialDenial(uint FlowId, uint LocalAddress, ushort LocalPort,
    DateTimeOffset From, DateTimeOffset Until);

internal readonly record struct TunWfpDrop(DateTimeOffset At, uint LocalAddress, ushort LocalPort,
    ulong FilterId, ushort LayerId, bool ConnectLayer, string Filter);

internal readonly record struct TunWfpUnattributedDrop(DateTimeOffset At, bool OtherImage);

internal static class TunWfpDiagnostic
{
    private static readonly Guid RemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid RemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid IpProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid ConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid ReceiveV4 = new("e1cd9fe7-f4b5-4273-96c0-592e485b8650");

    internal static uint Address(IPAddress ip) => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());

    internal static string[] Read(string image, IPAddress peer, int port, IPAddress source, TunDialDenial[] denied)
    {
        if (denied.Length == 0) return [];
        var status = FwpmEngineOpen0(IntPtr.Zero, 10, IntPtr.Zero, IntPtr.Zero, out var engine);
        if (status != 0) return Error("открытие WFP", status);
        try
        {
            status = FwpmEngineGetOption0(engine, 0, out var enabled);
            if (status != 0) return Error("чтение настройки сбора", status);
            try
            {
                if (Marshal.ReadInt32(enabled) != 3 || Marshal.ReadInt32(enabled, 8) == 0)
                    return ["WFP · сбор сетевых событий Windows выключен или его тип не подтверждён."];
            }
            finally { FwpmFreeMemory0(ref enabled); }

            status = FwpmGetAppIdFromFileName0(image, out var app);
            if (status != 0) return Error("идентификация образа Xray", status);
            try { return ReadEvents(engine, Marshal.PtrToStructure<Blob>(app), Address(peer), (ushort)port, Address(source), denied); }
            finally { FwpmFreeMemory0(ref app); }
        }
        finally { FwpmEngineClose0(engine); }
    }

    private static string[] ReadEvents(IntPtr engine, Blob app, uint peer, ushort port, uint source, TunDialDenial[] denied)
    {
        var start = denied.Min(item => item.From).AddMilliseconds(-500);
        var end = DateTimeOffset.UtcNow;
        var conditions = new Condition[] { new(RemoteAddress, 3, peer), new(RemotePort, 2, port), new(IpProtocol, 1, 6) };
        var stride = Marshal.SizeOf<Condition>();
        var memory = Marshal.AllocHGlobal(conditions.Length * stride);
        try
        {
            for (var i = 0; i < conditions.Length; ++i)
                Marshal.StructureToPtr(conditions[i], IntPtr.Add(memory, i * stride), false);
            var template = new EventTemplate {
                Start = start.ToFileTime(), End = end.ToFileTime(), Count = (uint)conditions.Length, Conditions = memory
            };
            var status = FwpmNetEventCreateEnumHandle0(engine, ref template, out var iterator);
            if (status != 0) return Error("выборка блокировок", status);
            try
            {
                const uint limit = 512;
                status = FwpmNetEventEnum0(engine, iterator, limit, out var entries, out var count);
                if (status != 0) return Error("чтение блокировок", status);
                try
                {
                    var drops = new List<TunWfpDrop>();
                    var unattributed = new List<TunWfpUnattributedDrop>();
                    var filters = new Dictionary<ulong, (bool Connect, string Label)>();
                    for (var i = 0; i < count && i < limit; i++)
                    {
                        var address = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        var item = Marshal.PtrToStructure<NetEvent>(address);
                        const uint required = 0x01 | 0x04 | 0x10 | 0x100; // protocol, remote address/port, IP version
                        const uint appIdSet = 0x20;
                        if (item.Type != 3 || item.Detail == IntPtr.Zero || (item.Flags & required) != required ||
                            item.IpVersion != 0 || item.Protocol != 6 || item.RemoteAddress != peer || item.RemotePort != port) continue;

                        var at = DateTimeOffset.FromFileTime(item.Timestamp);
                        var identified = (item.Flags & appIdSet) != 0 && item.App.Valid && app.Valid;
                        if (!identified || !SameApp(app, item.App))
                        {
                            // Keep only the time and whether the ID differed. A matching endpoint
                            // or TCP tuple cannot establish ownership when the image did not match.
                            unattributed.Add(new(at, OtherImage: identified));
                            continue;
                        }

                        var id = unchecked((ulong)Marshal.ReadInt64(item.Detail));
                        if (!filters.TryGetValue(id, out var filter))
                            filters[id] = filter = GetFilter(engine, id);
                        drops.Add(new(at, (item.Flags & 2) != 0 ? item.LocalAddress : 0,
                            (item.Flags & 8) != 0 ? item.LocalPort : (ushort)0, id,
                            unchecked((ushort)Marshal.ReadInt16(item.Detail, 8)), filter.Connect, filter.Label));
                    }
                    return Correlate(denied, drops, source, count >= limit, unattributed);
                }
                finally { if (entries != IntPtr.Zero) FwpmFreeMemory0(ref entries); }
            }
            finally { FwpmNetEventDestroyEnumHandle0(engine, iterator); }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static bool SameApp(Blob left, Blob right)
    {
        if (left.Size != right.Size) return false;
        for (var i = 0; i < left.Size; i++)
            if (Marshal.ReadByte(left.Data, i) != Marshal.ReadByte(right.Data, i)) return false;
        return true;
    }

    private static (bool Connect, string Label) GetFilter(IntPtr engine, ulong id)
    {
        if (FwpmFilterGetById0(engine, id, out var memory) != 0) return (false, "сведения о фильтре недоступны");
        try
        {
            var filter = Marshal.PtrToStructure<FilterHeader>(memory);
            var stage = filter.Layer == ConnectV4 ? "ALE_AUTH_CONNECT_V4" :
                filter.Layer == ReceiveV4 ? "ALE_AUTH_RECV_ACCEPT_V4" : $"слой {filter.Layer:D}";
            var name = Marshal.PtrToStringUni(filter.Name) switch {
                "Xray TUN: block allexceptlan" => "Xray TUN · block allexceptlan",
                "Xray TUN: permit Xray" => "Xray TUN · permit Xray",
                "Xray TUN: block the DNS Client service" => "Xray TUN · block the DNS Client service",
                string value when value.StartsWith("Xray TUN: block ", StringComparison.Ordinal) => "Xray TUN · другое правило блокировки",
                _ => "фильтр WFP"
            };
            var provider = filter.Provider != IntPtr.Zero ? $" · поставщик {Marshal.PtrToStructure<Guid>(filter.Provider):D}" : "";
            return (filter.Layer == ConnectV4, stage + " · " + name + provider);
        }
        finally { FwpmFreeMemory0(ref memory); }
    }

    internal static string[] Correlate(TunDialDenial[] denied, IEnumerable<TunWfpDrop> observed, uint source = 0,
        bool capped = false, IEnumerable<TunWfpUnattributedDrop>? unattributed = null)
    {
        bool InWindow(DateTimeOffset at) => denied.Any(attempt =>
            at >= attempt.From.AddMilliseconds(-500) && at <= attempt.Until.AddMilliseconds(500));
        var drops = observed.Where(drop => InWindow(drop.At)).ToArray();

        var lines = new List<string>();
        if (drops.Length == 0)
            lines.Add(capped ? "WFP · в прочитанной части выборки блокировки для образа Xray и IP:порта сервера не найдено." :
                "WFP · зарегистрированной блокировки для образа Xray и IP:порта сервера в окне отказа не найдено.");
        foreach (var group in drops.GroupBy(x => (x.FilterId, x.LayerId, x.Filter)).Take(8))
        {
            var ids = group.Select(drop => ExactFlow(drop, denied)).OfType<uint>().Distinct().Order().Take(12).ToArray();
            var match = ids.Length == 0 ? "совпадение по образу, назначению и времени · конкретный поток не установлен" :
                "совпадение по TCP-кортежу и времени · ID потока Xray " + string.Join(", ", ids);
            var origins = string.Join(", ", group.Select(x => x.LocalAddress == 0 ? "IPv4 источника отсутствует" :
                x.LocalAddress == source ? "ожидаемый IPv4 источника" :
                x.LocalAddress == Address(IPAddress.Parse(TunConfig.Address)) ? "IPv4 источника Wintun" : "иной IPv4 источника").Distinct());
            lines.Add($"WFP · блокировок {group.Count()} · фильтр {group.Key.FilterId}, слой {group.Key.LayerId} · {group.Key.Filter} · {origins} · {match}.");
        }
        if (unattributed != null)
        {
            var missing = 0;
            var other = 0;
            foreach (var entry in unattributed.Where(entry => InWindow(entry.At)))
            {
                if (entry.OtherImage) other++;
                else missing++;
            }
            if (missing + other > 0)
                lines.Add($"WFP · другие блокировки к IP:порту сервера в окне отказа · образ не определён: {missing}, " +
                    $"другой образ: {other}. Связь этих событий с запросом Xray не установлена.");
        }
        if (capped) lines.Add("WFP · выборка ограничена первыми 512 событиями.");
        return lines.ToArray();
    }

    private static uint? ExactFlow(TunWfpDrop drop, TunDialDenial[] denied)
    {
        if (!drop.ConnectLayer || drop.LocalAddress == 0 || drop.LocalPort == 0) return null;
        var flows = denied.Where(attempt => attempt.LocalAddress == drop.LocalAddress && attempt.LocalPort == drop.LocalPort &&
            drop.At >= attempt.From.AddMilliseconds(-500) && drop.At <= attempt.Until.AddMilliseconds(500))
            .Select(attempt => attempt.FlowId).Distinct().Take(2).ToArray();
        return flows.Length == 1 && flows[0] != 0 ? flows[0] : null;
    }

    private static string[] Error(string stage, uint code) => [$"WFP · {stage} недоступно · код 0x{code:X8}."];

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Condition(Guid key, uint type, uint value)
    {
        private readonly Guid field = key;
        private readonly uint match = 0;
        private readonly uint padding = 0;
        private readonly uint valueType = type;
        private readonly uint valuePadding = 0;
        private readonly ulong valueData = value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTemplate { internal long Start, End; internal uint Count; internal IntPtr Conditions; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        internal uint Size;
        internal IntPtr Data;
        internal readonly bool Valid => Size is > 0 and <= 32768 && Data != IntPtr.Zero;
    }

    // Windows SDK FWPM_NET_EVENT0, x64. The two address unions each reserve 16 bytes for IPv6.
    [StructLayout(LayoutKind.Explicit)]
    private struct NetEvent
    {
        [FieldOffset(0)] internal long Timestamp;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(12)] internal int IpVersion;
        [FieldOffset(16)] internal byte Protocol;
        [FieldOffset(20)] internal uint LocalAddress;
        [FieldOffset(36)] internal uint RemoteAddress;
        [FieldOffset(52)] internal ushort LocalPort;
        [FieldOffset(54)] internal ushort RemotePort;
        [FieldOffset(64)] internal Blob App;
        [FieldOffset(88)] internal int Type;
        [FieldOffset(96)] internal IntPtr Detail;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FilterHeader
    {
        [FieldOffset(16)] internal IntPtr Name;
        [FieldOffset(40)] internal IntPtr Provider;
        [FieldOffset(64)] internal Guid Layer;
    }

    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineOpen0(IntPtr server, uint auth,
        IntPtr identity, IntPtr session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineGetOption0(IntPtr engine, uint option, out IntPtr value);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmGetAppIdFromFileName0(string path, out IntPtr app);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventCreateEnumHandle0(IntPtr engine, ref EventTemplate template, out IntPtr iterator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventEnum0(IntPtr engine, IntPtr iterator, uint request,
        out IntPtr entries, out uint count);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventDestroyEnumHandle0(IntPtr engine, IntPtr iterator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterGetById0(IntPtr engine, ulong id, out IntPtr filter);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr memory);
}
