using System;
using System.Collections.Generic;
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
        private readonly Action<HostedTraceEvent> trace;
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

        internal HostedNetwork(bool monitor = false, Action<HostedTraceEvent> trace = null)
        {
            Check(WlanOpenHandle(2, IntPtr.Zero, out _, out handle), 0, "WlanOpenHandle");
            this.trace = trace;
            if (!monitor) return;
            notification = OnNotification;
            try
            {
                Check(WlanRegisterNotification(handle, HostedNotifications, false, notification, IntPtr.Zero,
                    IntPtr.Zero, out _), 0, "WlanRegisterNotification");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void OnNotification(ref NotificationData data, IntPtr context)
        {
            if (data.Source == HostedNotifications && trace != null)
            {
                var activity = HostedTraceEvent.Read(data.Interface, data.Code, data.Size, data.Data);
                if (activity != null) trace(activity);
            }
            if (data.Source != HostedNotifications || data.Code != HostedStateChange || data.Size < 12 ||
                data.Data == IntPtr.Zero) return;
            if (Marshal.ReadInt32(data.Data) != (int)State.Active ||
                Marshal.ReadInt32(data.Data, 4) == (int)State.Active) return;
            Volatile.Write(ref lastReason, Marshal.ReadInt32(data.Data, 8));
            Interlocked.Increment(ref interrupted);
        }

        internal State ReadPeers(out Guid adapter, out ulong[] clients)
        {
            Check(WlanHostedNetworkQueryStatus(handle, out var buffer, IntPtr.Zero), 0, "WlanHostedNetworkQueryStatus");
            try
            {
                var status = Marshal.PtrToStructure<StatusHeader>(buffer);
                adapter = status.Adapter;
                var authenticated = new List<ulong>();
                if (status.State == State.Active && status.PeerCount <= 100)
                    for (var i = 0; i < status.PeerCount; i++)
                    {
                        var peer = IntPtr.Add(buffer, Marshal.SizeOf(typeof(StatusHeader)) + i * 12);
                        if (Marshal.ReadInt32(peer, 8) == 1) authenticated.Add(ReadMac(peer));
                    }
                clients = authenticated.ToArray();
                return status.State;
            }
            finally { WlanFreeMemory(buffer); }
        }

        internal static ulong ReadMac(IntPtr address) =>
            unchecked((uint)Marshal.ReadInt32(address)) | ((ulong)unchecked((ushort)Marshal.ReadInt16(address, 4)) << 32);

        internal State Query(out Guid adapter) => Query(out adapter, out _);

        internal State Query(out Guid adapter, out uint peers)
        {
            var status = ReadStatus();
            adapter = status.Adapter;
            peers = status.PeerCount;
            return status.State;
        }

        private StatusHeader ReadStatus()
        {
            IntPtr buffer;
            Check(WlanHostedNetworkQueryStatus(handle, out buffer, IntPtr.Zero), 0, "WlanHostedNetworkQueryStatus");
            try
            {
                return Marshal.PtrToStructure<StatusHeader>(buffer);
            }
            finally { WlanFreeMemory(buffer); }
        }

        internal HostedTarget ActiveTarget()
        {
            var generation = Interruptions;
            var first = ReadStatus();
            if (first.State != State.Active || first.Adapter == Guid.Empty ||
                Array.TrueForAll(first.Mac, value => value == 0)) return null;
            var settings = ReadSettings();
            var last = ReadStatus();
            var bssid = BitConverter.ToString(first.Mac);
            if (settings.SsidLength == 0 || settings.SsidLength > 32 || last.State != State.Active ||
                last.Adapter != first.Adapter || BitConverter.ToString(last.Mac) != bssid || Interruptions != generation)
                return null;
            return new HostedTarget { Adapter = first.Adapter, Bssid = bssid,
                SsidBytes = Convert.ToBase64String(settings.Ssid, 0, (int)settings.SsidLength) };
        }

        internal void ForceStop()
        {
            var result = WlanHostedNetworkForceStop(handle, out var reason, IntPtr.Zero);
            if (result == 0) return;
            LocalStopError(result, reason);
        }

        private static void LocalStopError(uint result, uint reason)
        {
            var text = result == 5 || reason == 5 || reason == 23 ? "Windows запретила отключение Hosted Network. Проверьте права администратора и политику WLAN." :
                result == 1062 || reason == 24 ? "Служба «Автонастройка WLAN» недоступна." :
                "Windows не смогла отключить Hosted Network. Возможно, изменилось состояние адаптера или точки.";
            throw new WifiError($"{text} (WLAN {result}, причина {reason})");
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
                ref settings, out reason, IntPtr.Zero), reason, "WlanHostedNetworkSetProperty");
            var key = new byte[password.Length + 1];
            try
            {
                Encoding.ASCII.GetBytes(password, 0, password.Length, key, 0);
                Check(WlanHostedNetworkSetSecondaryKey(handle, (uint)key.Length, key, true, false,
                    out reason, IntPtr.Zero), reason, "WlanHostedNetworkSetSecondaryKey");
            }
            finally { Array.Clear(key, 0, key.Length); }
            Check(WlanHostedNetworkStartUsing(handle, out reason, IntPtr.Zero), reason, "WlanHostedNetworkStartUsing");
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
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out buffer), 0, "WlanEnumInterfaces");
            try { return Marshal.ReadInt32(buffer) > 0; }
            finally { WlanFreeMemory(buffer); }
        }

        internal string ReadRadio()
        {
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out var list), 0, "WlanEnumInterfaces");
            var enabled = 0;
            var softwareOff = 0;
            var hardwareOff = 0;
            var unknown = 0;
            try
            {
                // WLAN_INTERFACE_INFO_LIST has two DWORDs then entries (GUID, WCHAR[256], state).
                var count = Math.Min(32, Marshal.ReadInt32(list));
                for (var i = 0; i < count; i++)
                {
                    var id = Marshal.PtrToStructure<Guid>(IntPtr.Add(list, 8 + i * 532));
                    if (WlanQueryInterface(handle, ref id, 4, IntPtr.Zero, out var size, out var radio, out _) != 0)
                    {
                        unknown++;
                        continue;
                    }
                    try
                    {
                        if (size < 4) { unknown++; continue; }
                        var phys = Math.Min(Marshal.ReadInt32(radio), (int)Math.Min(64, (size - 4) / 12));
                        for (var j = 0; j < phys; j++)
                        {
                            var software = Marshal.ReadInt32(radio, 8 + j * 12);
                            var hardware = Marshal.ReadInt32(radio, 12 + j * 12);
                            if (software == 1 && hardware == 1) enabled++;
                            if (software == 2) softwareOff++;
                            if (hardware == 2) hardwareOff++;
                            if (software == 0 || hardware == 0) unknown++;
                        }
                    }
                    finally { WlanFreeMemory(radio); }
                }
            }
            finally { WlanFreeMemory(list); }
            return $"Радио WLAN по данным Windows (PHY) — включено {enabled}, программно выключено {softwareOff}, " +
                $"аппаратно выключено {hardwareOff}, состояние не подтверждено {unknown}.";
        }

        private Settings ReadSettings()
        {
            uint size, valueType;
            IntPtr buffer;
            Check(WlanHostedNetworkQueryProperty(handle, 0, out size, out buffer, out valueType, IntPtr.Zero),
                0, "WlanHostedNetworkQueryProperty");
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
            if (result != 0 && reason != 10) Check(result, reason, "WlanHostedNetworkStopUsing");
            started = false;
            return new HostedRelease(result, reason);
        }

        internal static string Explain(uint code, uint reason)
        {
            if (code == 1062 || reason == 24)
                return "Служба «Автонастройка WLAN» (WlanSvc) недоступна. Проверьте, запущена ли она, в services.msc.";
            if (code == 5 || reason == 5 || reason == 23)
                return "Windows запретила операцию WLAN. Проверьте права администратора и политику Hosted Network.";
            if (reason == 19)
                return "Драйвер или Windows отказались запустить виртуальную точку. Проверьте Hosted Network командой netsh wlan show drivers. " +
                    "Если поддержка есть, выключите конкурирующий хот-спот вручную и проверьте Ralink.";
            if (code == 5023)
                return "Текущее состояние WLAN не допускает операцию. Дождитесь появления адаптера и обновите диагностику.";
            if (reason == 13) return "Windows остановила минипорт виртуального Wi-Fi.";
            if (reason == 12) return "Интерфейс Wi-Fi стал недоступен для Hosted Network.";
            if (code == 50 || reason == 27)
                return "Hosted Network недоступна. Проверьте поддержку драйвера командой netsh wlan show drivers.";
            return "Ошибка Hosted Network. Проверьте адаптер Ralink и службу «Автонастройка WLAN».";
        }

        private static void Check(uint code, uint reason, string operation)
        {
            if (code != 0)
                throw new WlanFailure(code, reason, operation);
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
        private static extern uint WlanQueryInterface(IntPtr client, ref Guid adapter, uint opcode, IntPtr reserved,
            out uint size, out IntPtr data, out uint valueType);
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
        private static extern uint WlanHostedNetworkForceStop(IntPtr client, out uint reason, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkStopUsing(IntPtr client, out uint reason, IntPtr reserved);
    }

    internal sealed class WlanFailure : WifiError
    {
        internal readonly uint Code;
        internal readonly uint Reason;
        internal readonly string Operation;
        internal bool CanReadFresh => Code == 6 || Code == 31 || Code == 1062 || Code == 5023 || Reason == 24;

        internal WlanFailure(uint code, uint reason, string operation = "Hosted Network")
            : base(string.Format("{0} ({1}, WLAN {2}, причина {3})", HostedNetwork.Explain(code, reason), operation, code, reason))
        {
            Code = code;
            Reason = reason;
            Operation = operation;
        }
    }
}
