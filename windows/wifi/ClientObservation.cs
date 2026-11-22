using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PomogatorWifi
{
    internal enum ClientIpv4 { Unknown, AwaitingEvidence, LinkLocal, Reachable }

    internal sealed class HostedTraceEvent
    {
        internal const uint StateChange = 0x1000, PeerChange = 0x1001, RadioChange = 0x1002;
        internal readonly DateTimeOffset At = DateTimeOffset.Now;
        internal readonly Guid Interface;
        internal readonly uint Code;
        internal readonly int Before, After, Reason;

        private HostedTraceEvent(Guid adapter, uint code, int before, int after, int reason = 0)
        {
            Interface = adapter;
            Code = code;
            Before = before;
            After = after;
            Reason = reason;
        }

        internal bool PeerAuthenticated => Code == PeerChange && After == 1;

        internal static HostedTraceEvent Read(Guid adapter, uint code, uint size, IntPtr data)
        {
            if (data == IntPtr.Zero) return null;
            // WLAN_HOSTED_NETWORK_PEER_STATE has a 6-byte MAC, padding, then the 4-byte auth state.
            if (code == PeerChange && size >= 28)
                return new HostedTraceEvent(adapter, code, Marshal.ReadInt32(data, 8), Marshal.ReadInt32(data, 20),
                    Marshal.ReadInt32(data, 24));
            if (code == StateChange && size >= 12)
                return new HostedTraceEvent(adapter, code, Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4),
                    Marshal.ReadInt32(data, 8));
            if (code == RadioChange && size >= 8)
                return new HostedTraceEvent(adapter, code, Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));
            return null;
        }

        internal string Line()
        {
            var transition = Code == PeerChange ? $"станция: {Auth(Before)} → {Auth(After)}, причина WLAN {Reason}" :
                Code == StateChange ? $"точка: {Point(Before)} → {Point(After)}, причина WLAN {Reason}" :
                $"радио: программное {Radio(Before)}, аппаратное {Radio(After)}";
            return At.ToString("HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + " · Hosted Network · " + transition;
        }

        private static string Auth(int state) => state == 1 ? "аутентифицирована" : state == 0 ? "не подтверждена" : "неизвестно";
        private static string Point(int state) => state == 2 ? "запущена" : state == 1 ? "выключена" : state == 0 ? "недоступна" : "неизвестно";
        private static string Radio(int state) => state == 1 ? "включено" : state == 2 ? "выключено" : "неизвестно";
    }

    internal sealed class ClientObservation
    {
        private Guid adapter;
        private bool observed, active, authenticated, changed;
        private ClientIpv4 ipv4;

        internal void Sample(HostedNetwork.State state, Guid id, int clients, ClientIpv4 ip)
        {
            observed = true;
            active = state == HostedNetwork.State.Active;
            if (!active || id == Guid.Empty) return;
            if (adapter != Guid.Empty && adapter != id)
            {
                changed = true;
                ipv4 = ClientIpv4.Unknown;
                authenticated = false;
            }
            adapter = id;
            authenticated |= clients > 0;
            if (clients > 0 && ip > ipv4) ipv4 = ip;
        }

        internal void Observe(HostedTraceEvent activity)
        {
            // Some notification providers leave the interface empty.
            if (adapter != Guid.Empty && (activity.Interface == Guid.Empty || activity.Interface == adapter) &&
                activity.PeerAuthenticated) authenticated = true;
        }

        internal static ClientIpv4 ReadIpv4Table(IntPtr table, int index, ulong[] clients)
        {
            if (clients.Length == 0) return ClientIpv4.Unknown;
            var result = ClientIpv4.AwaitingEvidence;
            var rows = Math.Min(Marshal.ReadInt32(table), 8192);
            // MIB_IPNET_TABLE2 starts with a DWORD count and padding on x64.
            // MIB_IPNET_ROW2 is 88 bytes. Only NlnsReachable rows for current peers on this interface count.
            for (var i = 0; i < rows; i++)
            {
                var row = IntPtr.Add(table, 8 + 88 * i);
                if (Marshal.ReadInt16(row) != 2 || Marshal.ReadInt32(row, 28) != index ||
                    Marshal.ReadInt32(row, 72) != 6 || Marshal.ReadInt32(row, 76) != 5 ||
                    Array.IndexOf(clients, HostedNetwork.ReadMac(IntPtr.Add(row, 40))) < 0) continue;
                var first = Marshal.ReadByte(row, 4);
                if (first == 0 || first == 127 || first >= 224) continue;
                if (first == 169 && Marshal.ReadByte(row, 5) == 254) result = ClientIpv4.LinkLocal;
                else return ClientIpv4.Reachable;
            }
            return result;
        }

        internal IEnumerable<string> Lines()
        {
            yield return "Запуск точки — " + (!observed ? "WLAN не подтвердила состояние." :
                active ? "Hosted Network запущена в конце наблюдения." : "в конце наблюдения Hosted Network выключена или недоступна.");
            if (changed) yield return "Виртуальный адаптер сменился. Итог подключения относится к последнему подтверждённому адаптеру.";
            yield return "WPA2 — " + (authenticated ? "Windows видела аутентифицированного клиента." :
                "Windows не подтвердила аутентификацию клиента. Точная причина по этому наблюдению неизвестна.");
            switch (ipv4)
            {
                case ClientIpv4.Reachable: yield return "IPv4 — Windows видела достижимого клиента. Выдача адреса по DHCP отдельно не подтверждена."; break;
                case ClientIpv4.LinkLocal: yield return "IPv4 — у клиента виден адрес 169.254.x.x. Проверьте получение адреса на телефоне."; break;
                default: yield return "IPv4 — адрес клиента не подтверждён. Проверьте сведения о сети на телефоне после подключения."; break;
            }
            yield return authenticated ? "ICS — проверяйте доступ в интернет с телефона после подтверждения его IPv4." :
                "ICS — оценка доступа в интернет с телефона начинается после аутентификации и подтверждения IPv4.";
        }
    }
}
