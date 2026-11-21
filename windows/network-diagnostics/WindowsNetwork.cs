using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PomogatorNetworkDiagnostics
{
    internal static class WindowsNetwork
    {
        internal static Finding Ras()
        {
            var size = Marshal.SizeOf(typeof(RasConnection));
            var bytes = (uint)size;
            for (int attempt = 0; attempt < 3 && bytes <= size * 256; attempt++)
            {
                var buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    var allocated = bytes;
                    Marshal.WriteInt32(buffer, size);
                    uint count;
                    var code = RasEnumConnectionsW(buffer, ref bytes, out count);
                    if (code == 603) continue; // ERROR_BUFFER_TOO_SMALL
                    if (code != 0)
                        return Finding.Create(Outcome.Review, "Windows не вернула список сеансов RAS.",
                            codes: new[] { "RAS=" + code });
                    if (count > allocated / size) break;

                    var pppoe = 0;
                    var vpn = 0;
                    for (int index = 0; index < count; index++)
                    {
                        // Read the device type only. The other fields include the profile name and phonebook path.
                        var offset = Marshal.OffsetOf(typeof(RasConnection), nameof(RasConnection.DeviceType)).ToInt32();
                        var deviceType = Marshal.PtrToStringUni(IntPtr.Add(buffer, index * size + offset), 17).TrimEnd('\0');
                        if (string.Equals(deviceType, "PPPoE", StringComparison.OrdinalIgnoreCase)) pppoe++;
                        if (string.Equals(deviceType, "VPN", StringComparison.OrdinalIgnoreCase)) vpn++;
                    }

                    var details = new[] {
                        "Сеансы, которые RAS считает активными: " + count + "; PPPoE: " + pppoe + "; VPN: " + vpn + ".",
                        "Список не доказывает, что конкретный сеанс передаёт трафик. Сопоставьте с выбранными маршрутами и внешними проверками.",
                        "Браузерный VPN и часть сторонних VPN не отображаются в RAS. Имена профилей и пути к телефонным книгам опущены."
                    };
                    return Finding.Create(pppoe == 0 ? Outcome.Review : Outcome.Ok,
                        pppoe == 0 ? "RAS не вернула активный PPPoE-сеанс." : "RAS обнаружила активный PPPoE-сеанс.", details);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }

            return Finding.Create(Outcome.Review, "Список RAS изменился во время проверки или превышает лимит.",
                codes: new[] { "RAS=603" });
        }

        internal static Finding Hosted()
        {
            IntPtr handle;
            uint version;
            var code = WlanOpenHandle(2, IntPtr.Zero, out version, out handle);
            if (code != 0) return WlanError("WlanOpenHandle", code);
            try
            {
                IntPtr status;
                code = WlanHostedNetworkQueryStatus(handle, out status, IntPtr.Zero);
                if (code != 0) return WlanError("WlanHostedNetworkQueryStatus", code);
                try
                {
                    var info = (HostedStatus)Marshal.PtrToStructure(status, typeof(HostedStatus));
                    var details = new List<string>();
                    if (info.DeviceId != Guid.Empty) details.Add("GUID виртуального адаптера: " + info.DeviceId);
                    if (info.State == 2) details.Add("Активных Wi-Fi-клиентов по данным WLAN API: " + info.Peers + ".");
                    details.Add("Выполнено только чтение статуса. Проверка не запускает и не останавливает точку доступа.");
                    switch (info.State)
                    {
                        case 1: return Finding.Create(Outcome.Ok, "Hosted Network доступна и сейчас выключена.", details);
                        case 2: return Finding.Create(Outcome.Ok, "Hosted Network активна.", details);
                        default:
                            details.Add("Сопоставьте состояние WlanSvc, наличие Ralink и поддержку драйвера. Причина недоступности не подтверждена.");
                            return Finding.Create(Outcome.Review, "WLAN API сообщила, что Hosted Network недоступна.", details);
                    }
                }
                finally { WlanFreeMemory(status); }
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }

        private static Finding WlanError(string operation, uint code) =>
            Finding.Create(Outcome.Review, "Не удалось прочитать состояние Hosted Network.",
                new[] { operation + " вернула код " + code + ". Сопоставьте с состоянием WlanSvc и драйвера." },
                new[] { "WLAN=" + code });

        // Windows SDK Ras.h uses 4-byte packing and includes the Vista correlation GUID.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct RasConnection
        {
            internal uint Size;
            internal IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] internal string EntryName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] internal string DeviceType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] internal string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string PhoneBook;
            internal uint SubEntry;
            internal Guid EntryId;
            internal uint Flags;
            internal uint LuidLow;
            internal int LuidHigh;
            internal Guid CorrelationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HostedStatus
        {
            internal int State;
            internal Guid DeviceId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal byte[] Bssid;
            internal uint PhyType;
            internal uint Channel;
            internal uint Peers;
        }

        [DllImport("rasapi32.dll", ExactSpelling = true)]
        private static extern uint RasEnumConnectionsW(IntPtr connections, ref uint size, out uint count);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanHostedNetworkQueryStatus(IntPtr client, out IntPtr status, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr buffer);
    }
}
