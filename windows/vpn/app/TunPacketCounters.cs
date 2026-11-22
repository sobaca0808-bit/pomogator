using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Pomogator.Vpn;

internal sealed record TunPacketSample(ulong SentByWindows, ulong ReceivedByWindows,
    ulong OutputDiscards = 0, ulong InputDiscards = 0, ulong OutputErrors = 0, ulong InputErrors = 0);

internal static class TunPacketCounters
{
    internal static TunPacketSample? Read(Guid id)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var adapter = TunWindows.Find(id);
            if (adapter == null) return null;
            var index = TunWindows.Index(adapter);
            if (index <= 0) return null;
            var row = new InterfaceRow { Index = (uint)index };
            if (GetIfEntry2(ref row) != 0 || row.InterfaceId != TunConfig.AdapterId(id)) return null;
            return new TunPacketSample(
                Sum(row.OutputUnicast, row.OutputNonUnicast),
                Sum(row.InputUnicast, row.InputNonUnicast),
                row.OutputDiscards, row.InputDiscards, row.OutputErrors, row.InputErrors);
        }
        catch (NetworkInformationException) { return null; }
    }

    private static ulong Sum(ulong unicast, ulong nonUnicast) =>
        ulong.MaxValue - unicast < nonUnicast ? ulong.MaxValue : unicast + nonUnicast;

    // MIB_IF_ROW2 from netioapi.h. Retain the full native buffer, mapping only the fields used here.
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct InterfaceRow
    {
        [FieldOffset(8)] internal uint Index;
        [FieldOffset(12)] internal Guid InterfaceId;
        [FieldOffset(1216)] internal ulong InputUnicast;
        [FieldOffset(1224)] internal ulong InputNonUnicast;
        [FieldOffset(1232)] internal ulong InputDiscards;
        [FieldOffset(1240)] internal ulong InputErrors;
        [FieldOffset(1288)] internal ulong OutputUnicast;
        [FieldOffset(1296)] internal ulong OutputNonUnicast;
        [FieldOffset(1304)] internal ulong OutputDiscards;
        [FieldOffset(1312)] internal ulong OutputErrors;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref InterfaceRow row);
}
