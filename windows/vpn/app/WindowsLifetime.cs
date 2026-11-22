using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pomogator.Vpn;

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
    {
        int size = 0;
        const uint insufficient = 122;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0) != insufficient || size < 4) return false;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return false;
            var rows = Marshal.ReadInt32(table);
            for (var index = 0; index < rows && index < (size - 4) / 24; index++)
            {
                var row = IntPtr.Add(table, 4 + index * 24);
                var portWord = Marshal.ReadInt32(row, 8);
                var localPort = ((portWord & 0xff) << 8) | ((portWord >> 8) & 0xff);
                if (Marshal.ReadInt32(row, 4) == 0x0100007f && localPort == port && Marshal.ReadInt32(row, 20) == pid)
                    return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
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
