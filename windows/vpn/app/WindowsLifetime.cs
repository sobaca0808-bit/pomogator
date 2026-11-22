using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pomogator.Vpn;

[Flags]
internal enum PeerTcp
{
    None = 0, SynSent = 1, Established = 2, ExpectedSource = 4, TunSource = 8, OtherSource = 16, Seen = 32
}

internal static class WindowsLifetime
{
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinWorkingSet, MaxWorkingSet;
        internal uint ActiveProcesses;
        internal UIntPtr Affinity;
        internal uint Priority, Scheduling;
    }

    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }

    internal static IDisposable Attach(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
            !AssignProcessToJobObject(job, process.Handle))
        {
            job.Dispose();
            throw new VpnError("Windows не позволила привязать ядро к приложению. Для безопасного завершения процесс ядра остановлен.");
        }
        return job;
    }

    internal static bool OwnsLoopback(int port, int pid)
        => ReadTcpTable(3, row => row.LocalAddress == 0x0100007f && row.LocalPort == port && row.Pid == pid) == true;

    internal static PeerTcp? ReadPeerTcp(int pid, IPAddress peer, int port, IPAddress source)
    {
        var expectedRemote = BitConverter.ToInt32(peer.GetAddressBytes());
        var expectedLocal = BitConverter.ToInt32(source.GetAddressBytes());
        var tunLocal = BitConverter.ToInt32(IPAddress.Parse(TunConfig.Address).GetAddressBytes());
        var result = PeerTcp.None;
        var read = ReadTcpTable(5, row =>
        {
            if (row.Pid != pid || row.RemoteAddress != expectedRemote || row.RemotePort != port) return false;
            result |= PeerTcp.Seen;
            result |= row.LocalAddress == expectedLocal ? PeerTcp.ExpectedSource :
                row.LocalAddress == tunLocal ? PeerTcp.TunSource :
                row.LocalAddress == 0 ? PeerTcp.None : PeerTcp.OtherSource;
            if (row.State == 3) result |= PeerTcp.SynSent;
            if (row.State == 5) result |= PeerTcp.Established;
            return false;
        });
        return read.HasValue ? result : null;
    }

    private readonly record struct TcpOwnerRow(int State, int LocalAddress, int LocalPort, int RemoteAddress, int RemotePort, int Pid);

    private static int ReadPort(IntPtr row, int offset)
        => (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, offset));

    private static bool? ReadTcpTable(uint tableClass, Func<TcpOwnerRow, bool> match)
    {
        int size = 0;
        const uint insufficient = 122;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, tableClass, 0) != insufficient) return null;
        for (var attempt = 0; size >= 4 && attempt < 3; attempt++)
        {
            var capacity = size;
            var table = Marshal.AllocHGlobal(capacity);
            try
            {
                var status = GetExtendedTcpTable(table, ref size, false, 2, tableClass, 0);
                if (status == insufficient) continue;
                if (status != 0) return null;
                var rows = Math.Min(Marshal.ReadInt32(table), (capacity - 4) / 24);
                for (var index = 0; index < rows; index++)
                {
                    var row = IntPtr.Add(table, 4 + index * 24);
                    if (match(new TcpOwnerRow(Marshal.ReadInt32(row), Marshal.ReadInt32(row, 4), ReadPort(row, 8),
                        Marshal.ReadInt32(row, 12), ReadPort(row, 16), Marshal.ReadInt32(row, 20)))) return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, uint tableClass, uint reserved);
}
