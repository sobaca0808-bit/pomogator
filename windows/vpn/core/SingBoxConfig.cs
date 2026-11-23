using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Pomogator.Vpn;

public static class SingBoxConfig
{
    public const string Version = "1.14.4-pomogator";
    public const string Executable = "PomogatorTunCore.exe";
    public const string Gateway = "198.18.240.2";

    public static void Validate(Profile profile)
    {
        if (profile.Transport != "raw" || profile.Security is not ("reality" or "tls"))
            throw new VpnError("Экспериментальный TUN sing-box поддерживает VLESS по TCP с TLS или REALITY.");
        if (profile.Security == "reality" && profile.Option("fp", "chrome") is not ("chrome" or "firefox"))
            throw new VpnError("Для REALITY в экспериментальном TUN проверяются отпечатки Chrome и Firefox.");
    }

    public static string Preflight(Profile profile, IPAddress peer, string uplink, int port) =>
        Create(Outbound(profile, peer, uplink), [Loopback(port)], [], [], false);

    public static string Tunnel(Profile profile, IPAddress peer, string uplink, Guid id, int port, TunControlEndpoint control) =>
        Tunnel(Outbound(profile, peer, uplink), TunConfig.Adapter(id), TunConfig.Routes(peer), peer, port, control,
            TunConfig.Resolver, new {
                type = "https", tag = "private-dns", server = TunConfig.Resolver, detour = "vpn",
                tls = new { enabled = true, server_name = "dns.quad9.net" }
            });

    internal static Dictionary<string, object> Outbound(Profile profile, IPAddress peer, string uplink)
    {
        Validate(profile);
        if (peer.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(peer) || string.IsNullOrWhiteSpace(uplink))
            throw new VpnError("Экспериментальный TUN требует подтверждённого исходящего интерфейса IPv4.");
        var pinned = TunConfig.Pinned(profile, peer);
        var tls = new Dictionary<string, object> { ["enabled"] = true,
            ["server_name"] = pinned.Option("sni", profile.Host) };
        if (pinned.Option("alpn") is { Length: > 0 } alpn) tls["alpn"] = alpn.Split(',');
        if (pinned.Security == "reality")
        {
            tls["utls"] = new { enabled = true, fingerprint = pinned.Option("fp", "chrome") };
            tls["reality"] = new { enabled = true, public_key = pinned.Option("pbk"), short_id = pinned.Option("sid") };
        }
        else if (pinned.Option("fp") is { Length: > 0 } fp)
            tls["utls"] = new { enabled = true, fingerprint = fp };
        return new() { ["type"] = "vless", ["tag"] = "vpn", ["server"] = peer.ToString(),
            ["server_port"] = pinned.Port, ["uuid"] = pinned.Id.ToString("D"), ["flow"] = pinned.Option("flow"),
            ["bind_interface"] = uplink, ["tls"] = tls };
    }

    internal static string Tunnel(object outbound, string adapter, string[] routes, IPAddress peer, int port,
        TunControlEndpoint control, string resolver, object dns)
    {
        if (!IPAddress.TryParse(control.Destination, out var target) || target.AddressFamily != AddressFamily.InterNetwork ||
            control.Port is < 1 or > 65535 || control.DestinationPort is < 1 or > 65535 ||
            control.Port == port || string.IsNullOrEmpty(control.User) || string.IsNullOrEmpty(control.Password))
            throw new ArgumentException("Invalid control inlet.", nameof(control));
        var tun = new { type = "tun", tag = "tun", interface_name = adapter, address = new[] { TunConfig.Address + "/30" },
            mtu = 1400, auto_route = true, strict_route = true, stack = "gvisor", dns_mode = "hijack",
            dns_address = new[] { resolver }, route_address = routes };
        var inlet = new { type = "http", tag = "control", listen = "127.0.0.1", listen_port = control.Port,
            users = new[] { new { username = control.User, password = control.Password } } };
        return Create(outbound, [tun, inlet, Loopback(port)], [
            new { inbound = new[] { "ready" }, action = "reject" },
            new { inbound = new[] { "control" }, ip_cidr = new[] { target + "/32" }, network = new[] { "tcp" },
                port = new[] { control.DestinationPort }, action = "route", outbound = "vpn" },
            new { inbound = new[] { "control" }, action = "reject" },
            new { inbound = new[] { "tun" }, ip_cidr = new[] { peer + "/32", "::/0" }, action = "reject" },
            new { inbound = new[] { "tun" }, port = new[] { 53 }, action = "hijack-dns" }
        ], [dns], true);
    }

    private static object Loopback(int port) => new { type = "http", tag = "ready", listen = "127.0.0.1", listen_port = port };

    private static string Create(object outbound, object[] inbounds, object[] rules, object[] dns, bool tun) =>
        JsonSerializer.Serialize(new {
            log = new { level = tun ? "info" : "error", timestamp = false },
            inbounds, outbounds = new[] { outbound },
            route = new { auto_detect_interface = tun, rules, final = "vpn" },
            dns = tun ? new { servers = dns, final = "private-dns" } : null
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
}
