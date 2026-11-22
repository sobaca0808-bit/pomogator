using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PomogatorWifi
{
    internal sealed class HostedNetwork : IDisposable
    {
        internal enum State { Unavailable, Idle, Active }
        private IntPtr handle;
        private bool started;
        private readonly NotificationCallback notification;
        private long interrupted;
        private int lastReason;
        internal long Interruptions => Interlocked.Read(ref interrupted);
        internal int InterruptionReason => Volatile.Read(ref lastReason);

        private const uint HostedNotifications = 0x80;
        private const uint HostedStateChange = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct NotificationData
        {
            internal uint Source, Code;
            internal Guid Interface;
            internal uint Size;
            internal IntPtr Data;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void NotificationCallback(ref NotificationData data, IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        private struct StatusHeader
        {
            internal State State;
            internal Guid Adapter;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            internal byte[] Mac;
            internal uint Phy, Frequency, PeerCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Settings
        {
            internal uint SsidLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            internal byte[] Ssid;
            internal uint MaxPeers;
        }

        internal HostedNetwork(bool monitor = false)
        {
            Check(WlanOpenHandle(2, IntPtr.Zero, out _, out handle), 0);
            if (!monitor) return;
            notification = OnNotification;
            try
            {
                Check(WlanRegisterNotification(handle, HostedNotifications, false, notification, IntPtr.Zero,
                    IntPtr.Zero, out _), 0);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void OnNotification(ref NotificationData data, IntPtr context)
        {
            if (data.Source != HostedNotifications || data.Code != HostedStateChange || data.Size < 12 ||
                data.Data == IntPtr.Zero) return;
            if (Marshal.ReadInt32(data.Data) != (int)State.Active ||
                Marshal.ReadInt32(data.Data, 4) == (int)State.Active) return;
            Volatile.Write(ref lastReason, Marshal.ReadInt32(data.Data, 8));
            Interlocked.Increment(ref interrupted);
        }

        internal State Query(out Guid adapter) => Query(out adapter, out _);

        internal State Query(out Guid adapter, out uint peers)
        {
            IntPtr buffer;
            Check(WlanHostedNetworkQueryStatus(handle, out buffer, IntPtr.Zero), 0);
            try
            {
                var status = (StatusHeader)Marshal.PtrToStructure(buffer, typeof(StatusHeader));
                adapter = status.Adapter;
                peers = status.PeerCount;
                return status.State;
            }
            finally { WlanFreeMemory(buffer); }
        }

        internal void Start(byte[] ssid, string password)
        {
            Guid adapter;
            var status = Query(out adapter);
            if (status == State.Active)
                throw new WifiError("Точка доступа уже запущена другим способом. Сначала завершите её там, где запускали.");
            if (status != State.Idle)
                throw new WifiError("Hosted Network недоступна. Проверьте Ralink, службу «Автонастройка WLAN» и поддержку драйвера командой netsh wlan show drivers.");

            var settings = ReadSettings();
            settings.SsidLength = (uint)ssid.Length;
            Array.Clear(settings.Ssid, 0, settings.Ssid.Length);
            Array.Copy(ssid, settings.Ssid, ssid.Length);
            uint reason;
            Check(WlanHostedNetworkSetProperty(handle, 0, (uint)Marshal.SizeOf(typeof(Settings)),
                ref settings, out reason, IntPtr.Zero), reason);
            var key = new byte[password.Length + 1];
            try
            {
                Encoding.ASCII.GetBytes(password, 0, password.Length, key, 0);
                Check(WlanHostedNetworkSetSecondaryKey(handle, (uint)key.Length, key, true, false,
                    out reason, IntPtr.Zero), reason);
            }
            finally { Array.Clear(key, 0, key.Length); }
            Check(WlanHostedNetworkStartUsing(handle, out reason, IntPtr.Zero), reason);
            started = true;
        }

        internal string Ssid()
        {
            var settings = ReadSettings();
            return Encoding.UTF8.GetString(settings.Ssid, 0, (int)Math.Min(settings.SsidLength, 32u));
        }

        internal bool HasWifi()
        {
            IntPtr buffer;
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out buffer), 0);
            try { return Marshal.ReadInt32(buffer) > 0; }
            finally { WlanFreeMemory(buffer); }
        }

        private Settings ReadSettings()
        {
            uint size, valueType;
            IntPtr buffer;
            Check(WlanHostedNetworkQueryProperty(handle, 0, out size, out buffer, out valueType, IntPtr.Zero), 0);
            try
            {
                if (size < Marshal.SizeOf(typeof(Settings)))
                    throw new WifiError("Windows вернула неполные параметры точки доступа.");
                return (Settings)Marshal.PtrToStructure(buffer, typeof(Settings));
            }
            finally { WlanFreeMemory(buffer); }
        }

        internal HostedRelease Stop()
        {
            if (!started) return new HostedRelease(0, 10);
            uint reason;
            var result = WlanHostedNetworkStopUsing(handle, out reason, IntPtr.Zero);
            // Windows can end the session first, for example after unplugging the adapter.
            if (result != 0 && reason != 10) Check(result, reason);
            started = false;
            return new HostedRelease(result, reason);
        }

        internal static string Explain(uint code, uint reason)
        {
            if (code == 1062 || reason == 24)
                return "Служба «Автонастройка WLAN» недоступна. Проверьте её в services.msc.";
            if (code == 5 || reason == 5 || reason == 23)
                return "Windows запретила операцию WLAN. Проверьте права администратора и политику Hosted Network.";
            if (code == 50 || reason == 12 || reason == 13 || reason == 19 || reason == 27)
                return "Адаптер не смог запустить Hosted Network. Проверьте подключение Ralink и поддержку драйвера командой netsh wlan show drivers.";
            return "Ошибка Hosted Network. Проверьте адаптер Ralink и службу «Автонастройка WLAN».";
        }

        private static void Check(uint code, uint reason)
        {
            if (code != 0)
                throw new WlanFailure(code, reason);
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            WlanCloseHandle(handle, IntPtr.Zero);
            GC.KeepAlive(notification);
            handle = IntPtr.Zero;
            started = false;
        }

        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr interfaces);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr pointer);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkQueryStatus(IntPtr client, out IntPtr status, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkQueryProperty(IntPtr client, uint opcode, out uint size,
            out IntPtr property, out uint valueType, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkSetProperty(IntPtr client, uint opcode, uint size,
            ref Settings settings, out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkSetSecondaryKey(IntPtr client, uint length, byte[] key,
            [MarshalAs(UnmanagedType.Bool)] bool passphrase, [MarshalAs(UnmanagedType.Bool)] bool persistent,
            out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanRegisterNotification(IntPtr client, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate,
            NotificationCallback callback, IntPtr context, IntPtr reserved, out uint previous);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkStartUsing(IntPtr client, out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkStopUsing(IntPtr client, out uint reason, IntPtr reserved);
    }

    internal sealed class WlanFailure : WifiError
    {
        internal WlanFailure(uint code, uint reason)
            : base(string.Format("{0} (WLAN {1}, причина {2})", HostedNetwork.Explain(code, reason), code, reason)) { }
    }
}
