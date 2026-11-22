using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PomogatorWifiLite
{
    internal sealed class Wlan : IHostedNetwork
    {
        private const uint HostedSource = 0x80;
        private IntPtr handle;
        private readonly NotificationHandler notification;
        private int ended, stateReason = -1, peerReason = -1, peerState;
        internal Wlan()
        {
            notification = OnNotification;
            Check(WlanOpenHandle(2, IntPtr.Zero, out _, out handle), "WlanOpenHandle");
        }

        public bool Interrupted => Volatile.Read(ref ended) != 0;

        public PointStatus Read()
        {
            var code = WlanHostedNetworkQueryStatus(handle, out var memory, IntPtr.Zero);
            if (code == 5023)
                throw new WifiException(Describe(code, "WlanHostedNetworkQueryStatus") + " " + RadioNote());
            Check(code, "WlanHostedNetworkQueryStatus");
            try
            {
                var info = Marshal.PtrToStructure<Status>(memory);
                var detail = info.State == PointState.Unavailable ? RadioNote() : null;
                var lost = Volatile.Read(ref stateReason);
                var lastPeer = Volatile.Read(ref peerReason);
                if (lost >= 0) detail = $"Windows сообщила об остановке точки. Причина WLAN {lost}. " + Hint(0, (uint)lost);
                else if (lastPeer >= 0) detail = (Volatile.Read(ref peerState) == 1 ?
                    "Последнее событие станции: аутентификация подтверждена." : "Последнее событие станции: аутентификация не подтверждена.") +
                    $" Код причины WLAN {lastPeer}.";
                var clients = info.State == PointState.Active ?
                    CountAuthenticated(IntPtr.Add(memory, Marshal.SizeOf<Status>()), info.Peers) : 0;
                return new PointStatus { State = info.State, Adapter = info.Adapter, Clients = clients, Detail = detail };
            }
            finally { WlanFreeMemory(memory); }
        }

        internal static uint CountAuthenticated(IntPtr peers, uint total)
        {
            uint count = 0;
            // WLAN_HOSTED_NETWORK_PEER_STATE has a 6-byte MAC, padding and a 4-byte auth state.
            for (var index = 0; index < total; index++)
                if (Marshal.ReadInt32(peers, checked(index * 12 + 8)) == 1) count++;
            return count;
        }

        public void StartUsing(byte[] ssid, string password)
        {
            Check(WlanRegisterNotification(handle, HostedSource, false, notification, IntPtr.Zero, IntPtr.Zero, out _),
                "WlanRegisterNotification");
            if (Read().State != PointState.Idle)
                throw new WifiException("Windows больше не показывает свободную Hosted Network.");
            // Preserve the Windows peer limit and change only the user-selected network name.
            Check(WlanHostedNetworkQueryProperty(handle, 0, out var size, out var memory, out _, IntPtr.Zero),
                "WlanHostedNetworkQueryProperty");
            ConnectionSettings settings;
            try
            {
                if (size < Marshal.SizeOf<ConnectionSettings>()) throw new WifiException("Windows вернула неполные параметры Hosted Network.");
                settings = Marshal.PtrToStructure<ConnectionSettings>(memory);
            }
            finally { WlanFreeMemory(memory); }
            settings.Length = (uint)ssid.Length;
            Array.Clear(settings.Ssid, 0, settings.Ssid.Length);
            Array.Copy(ssid, settings.Ssid, ssid.Length);
            Check(WlanHostedNetworkSetProperty(handle, 0, (uint)Marshal.SizeOf<ConnectionSettings>(),
                ref settings, out var reason, IntPtr.Zero), "WlanHostedNetworkSetProperty", reason);
            var key = new byte[password.Length + 1]; // WPA2 passphrase form includes a terminating zero.
            try
            {
                Encoding.ASCII.GetBytes(password, 0, password.Length, key, 0);
                Check(WlanHostedNetworkSetSecondaryKey(handle, (uint)key.Length, key, true, false, out reason, IntPtr.Zero),
                    "WlanHostedNetworkSetSecondaryKey", reason);
            }
            finally { Array.Clear(key, 0, key.Length); }
            if (Read().State != PointState.Idle || Interrupted)
                throw new WifiException("Состояние Hosted Network изменилось до запуска. Проверьте, не открыта ли другая точка.");
            Check(WlanHostedNetworkStartUsing(handle, out reason, IntPtr.Zero), "WlanHostedNetworkStartUsing", reason);
        }

        public void StopUsing()
        {
            var code = WlanHostedNetworkStopUsing(handle, out var reason, IntPtr.Zero);
            if (reason != 10) Check(code, "WlanHostedNetworkStopUsing", reason); // Windows already released this reference.
        }

        private void OnNotification(ref Notification message, IntPtr context)
        {
            if (message.Source != HostedSource || message.Data == IntPtr.Zero) return;
            if (message.Code == 0x1000 && message.Size >= 12 &&
                Marshal.ReadInt32(message.Data) == 2 && Marshal.ReadInt32(message.Data, 4) != 2)
            {
                Volatile.Write(ref stateReason, Marshal.ReadInt32(message.Data, 8));
                Volatile.Write(ref ended, 1);
            }
            if (message.Code == 0x1001 && message.Size >= 28)
            {
                Volatile.Write(ref peerState, Marshal.ReadInt32(message.Data, 20));
                Volatile.Write(ref peerReason, Marshal.ReadInt32(message.Data, 24));
            }
        }

        private string RadioNote()
        {
            const string prefix = "Hosted Network недоступна (состояние WLAN 0). ";
            var code = WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (code != 0) return prefix + Describe(code, "WlanEnumInterfaces");
            try
            {
                var count = Math.Min(Marshal.ReadInt32(list), 32);
                if (count == 0) return prefix + "Windows не видит беспроводной адаптер. Проверьте USB и состояние Ralink.";
                bool softwareOff = false, hardwareOff = false, enabled = false;
                for (var index = 0; index < count; index++)
                {
                    // WLAN_INTERFACE_INFO_LIST has two DWORDs then entries (GUID, WCHAR[256], enum).
                    var id = Marshal.PtrToStructure<Guid>(IntPtr.Add(list, 8 + index * 532));
                    if (WlanQueryInterface(handle, ref id, 4, IntPtr.Zero, out var size, out var info, out _) != 0) continue;
                    try
                    {
                        if (size < 4) continue;
                        var phys = Math.Min(Marshal.ReadInt32(info), (int)Math.Min(64u, (size - 4) / 12));
                        for (var i = 0; i < phys; i++)
                        {
                            var software = Marshal.ReadInt32(info, 8 + i * 12);
                            var hardware = Marshal.ReadInt32(info, 12 + i * 12);
                            softwareOff |= software == 2;
                            hardwareOff |= hardware == 2;
                            enabled |= software == 1 && hardware == 1;
                        }
                    }
                    finally { WlanFreeMemory(info); }
                }
                if (!enabled && hardwareOff) return prefix + "Windows сообщает, что аппаратное радио выключено.";
                if (!enabled && softwareOff) return prefix + "Windows сообщает, что программное радио выключено.";
                return prefix + "Проверьте Ralink и поддержку Hosted Network командой netsh wlan show drivers.";
            }
            finally { WlanFreeMemory(list); }
        }

        internal static string Describe(uint code, string operation, uint? reason = null) =>
            $"{operation} · Win32 {code}" + (reason.HasValue ? $" · причина WLAN {reason}" : "") + ". " +
            new Win32Exception(unchecked((int)code)).Message + " " + Hint(code, reason);

        private static string Hint(uint code, uint? reason)
        {
            if (code == 1062 || reason == 24) return "Проверьте службу «Автонастройка WLAN» (WlanSvc).";
            if (reason == 19) return "Windows или драйвер отклонили запуск виртуальной точки. Проверьте радио и состояние Ralink.";
            if (reason == 12 || reason == 13) return "Windows сообщила о недоступности интерфейса или остановке минипорта.";
            if (code == 5 || reason == 5 || reason == 23) return "Проверьте права и политику Hosted Network.";
            if (code == 5023 || code == 50 || reason == 27) return "Проверьте состояние адаптера и поддержку Hosted Network.";
            return "";
        }

        private static void Check(uint code, string operation, uint? reason = null)
        {
            if (code != 0) throw new WifiException(Describe(code, operation, reason));
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            WlanCloseHandle(handle, IntPtr.Zero);
            GC.KeepAlive(notification);
            handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Status
        {
            internal PointState State;
            internal Guid Adapter;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal byte[] Bssid;
            internal uint Phy, Frequency, Peers;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ConnectionSettings
        {
            internal uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Ssid;
            internal uint MaxPeers;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Notification { internal uint Source, Code; internal Guid Interface; internal uint Size; internal IntPtr Data; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void NotificationHandler(ref Notification message, IntPtr context);
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid id, uint opcode, IntPtr reserved,
            out uint length, out IntPtr memory, out uint type);
        [DllImport("wlanapi.dll")] private static extern uint WlanHostedNetworkQueryStatus(IntPtr client, out IntPtr status, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanHostedNetworkQueryProperty(IntPtr client, uint opcode,
            out uint size, out IntPtr property, out uint type, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanHostedNetworkSetProperty(IntPtr client, uint opcode, uint size,
            ref ConnectionSettings settings, out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanHostedNetworkSetSecondaryKey(IntPtr client, uint length, byte[] key,
            [MarshalAs(UnmanagedType.Bool)] bool passphrase, [MarshalAs(UnmanagedType.Bool)] bool persistent, out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanHostedNetworkStartUsing(IntPtr client, out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanHostedNetworkStopUsing(IntPtr client, out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(IntPtr client, uint source,
            [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate, NotificationHandler callback, IntPtr context, IntPtr reserved, out uint previous);
    }
}
