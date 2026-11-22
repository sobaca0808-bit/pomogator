using System.Runtime.InteropServices;

namespace Pomogator.Vpn;

internal static class RasProxyScope
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct Connection
    {
        internal int Size;
        internal IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] internal string Entry;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] internal string DeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] internal string Device;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string PhoneBook;
        internal int SubEntry;
        internal Guid EntryId;
        internal int Flags, LuidLow, LuidHigh;
        internal Guid Correlation;
    }

    internal static string? Active()
    {
        int size = Marshal.SizeOf<Connection>(), bytes = size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, size);
            var result = RasEnumConnections(buffer, ref bytes, out var count);
            if (result == 603 && bytes > size && bytes <= size * 64)
            {
                buffer = Marshal.ReAllocHGlobal(buffer, (IntPtr)bytes);
                Marshal.WriteInt32(buffer, size);
                result = RasEnumConnections(buffer, ref bytes, out count);
            }
            if (result != 0) throw new VpnError("Не удалось определить активное подключение Windows (RAS " + result + ").");
            return Select(Enumerable.Range(0, count).Select(i => Marshal.PtrToStructure<Connection>(
                IntPtr.Add(buffer, i * size)).Entry));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string? Select(IEnumerable<string> entries)
    {
        var names = entries.Where(entry => !string.IsNullOrWhiteSpace(entry)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        if (names.Length > 1) throw new VpnError("Windows сообщает о нескольких активных подключениях RAS. Безопасный выбор системного прокси невозможен.");
        return names.SingleOrDefault();
    }

    [DllImport("rasapi32.dll", EntryPoint = "RasEnumConnectionsW")]
    private static extern int RasEnumConnections(IntPtr connections, ref int bytes, out int count);
}
