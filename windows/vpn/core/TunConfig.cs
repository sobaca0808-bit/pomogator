using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Pomogator.Vpn;

public sealed record TunControlEndpoint(int Port, string User, string Password, string Destination, int DestinationPort);

public static class TunConfig
{
    public const string Address = "198.18.240.1";
    public const int PrefixLength = 30;
    public const string InternetProbe = "1.1.1.1";
    public const string InternetProbeHost = "cloudflare-dns.com";
    private const string DnsUpstream = "https://dns.quad9.net/dns-query";
    private const string DnsIngress = "pmt-doh";
    private const string DnsOutbound = "pmt-dns";
    public const string Resolver = "9.9.9.9";
    private const string TunTag = "pomogator-tun";
    private const string ControlTag = "pmt-tun-control";

    public static string Adapter(Guid id) => "PmtVpn-" + id.ToString("N")[..16];

    public static Guid AdapterId(Guid id) => new(System.Security.Cryptography.MD5.HashData(
        System.Text.Encoding.UTF8.GetBytes(Adapter(id))));

    public static string[] Routes(IPAddress peer)
    {
        if (peer.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4 required.", nameof(peer));
        var holes = new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
            "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/3", peer + "/32" }
            .Select(IPNetwork.Parse)
            .Select(range => (Start: Number(range.BaseAddress), Size: 1UL << (32 - range.PrefixLength))).ToArray();
        var result = new List<string>();
        Add(0, 0);
        return result.ToArray();

        void Add(ulong start, int bits)
        {
            var size = 1UL << (32 - bits);
            if (holes.Any(hole => hole.Start <= start && hole.Start + hole.Size >= start + size)) return;
            if (!holes.Any(hole => hole.Start < start + size && start < hole.Start + hole.Size))
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)start);
                result.Add(new IPAddress(bytes) + "/" + bits);
                return;
            }
            Add(start, bits + 1);
            Add(start + size / 2, bits + 1);
        }
    }

    private static ulong Number(IPAddress ip) => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());

    public static Profile Pinned(Profile profile, IPAddress peer)
    {
        var options = new Dictionary<string, string>(profile.Options);
        if (profile.Security == "tls" && profile.Option("sni") == "") options["sni"] = profile.Host;
        return profile with { Host = peer.ToString(), Options = options, Full = null };
    }

    public static string Engine(Guid id, IPAddress peer, IPAddress source, string uplink, Profile profile, int readiness,
        int diagnosticsPort = 0, TunControlEndpoint? control = null) =>
        CreateEngine(id, Routes(peer), new[] { "allexceptlan", "[::/0]:all", "misconfigtun" },
            UplinkOutbound(peer, source, uplink, profile), readiness,
            diagnosticsPort: diagnosticsPort > 0 ? diagnosticsPort : null, dnsOverHttps: DnsUpstream, control: control, peer: peer);

    public static string PreflightEngine(IPAddress peer, IPAddress source, string uplink, Profile profile, int port) => JsonSerializer.Serialize(new {
        log = new { loglevel = "none" },
        inbounds = new[] { new { listen = "127.0.0.1", port, protocol = "http" } },
        outbounds = new[] { UplinkOutbound(peer, source, uplink, profile) }
    });

    internal static Dictionary<string, object> UplinkOutbound(IPAddress peer, IPAddress source, string uplink, Profile profile)
    {
        if (source.AddressFamily != AddressFamily.InterNetwork || source.Equals(IPAddress.Any) ||
            IPAddress.IsLoopback(source) || source.Equals(IPAddress.Parse(Address)))
            throw new ArgumentException("An IPv4 address of the uplink is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(uplink))
            throw new ArgumentException("A verified IPv4 interface is required.", nameof(uplink));
        var outbound = XrayConfig.Outbound(Pinned(profile, peer), "vpn");
        outbound["sendThrough"] = source.ToString();
        // The Windows Xray dialer uses IP_UNICAST_IF on this outbound only. The
        // global TUN interface guard can change WeakHostSend on the uplink.
        var stream = (Dictionary<string, object>)outbound["streamSettings"];
        stream["sockopt"] = new { @interface = uplink };
        return outbound;
    }

    // Production and native CI use the same TUN, routing and optional StatsService.
    internal static string CreateEngine(Guid id, string[] routes, string[] filters, object outbound, int readiness,
        string resolver = Resolver, int? diagnosticsPort = null, string? dnsOverHttps = null, TunControlEndpoint? control = null,
        IPAddress? peer = null)
    {
        var outbounds = new List<object> { outbound, new { tag = "block", protocol = "blackhole" } };
        var rules = new List<object> {
            new { type = "field", inboundTag = new[] { "readiness" }, outboundTag = "block" },
            new { type = "field", ip = new[] { "::/0" }, outboundTag = "block" }
        };
        var inbounds = new List<object> {
            new { protocol = "tun", tag = TunTag, port = 0, settings = new {
                name = Adapter(id), desc = "PomogatorVpn", mtu = 1400,
                gateway = new[] { Address + "/" + PrefixLength }, dns = new[] { resolver },
                autoSystemRoutingTable = routes, autoOutboundsInterface = "",
                autoSystemWfpBlockLeak = filters
            } },
            new { listen = "127.0.0.1", port = readiness, protocol = "http", tag = "readiness" }
        };
        if (peer != null)
            rules.Add(new { type = "field", ruleTag = "pmt-peer-reentry", inboundTag = new[] { TunTag },
                ip = new[] { peer + "/32" }, outboundTag = "block" });
        if (control != null)
        {
            if (!IPAddress.TryParse(control.Destination, out var target) || target.AddressFamily != AddressFamily.InterNetwork ||
                control.DestinationPort is < 1 or > 65535 || control.Port is < 1 or > 65535 ||
                control.User.Length == 0 || control.Password.Length == 0)
                throw new ArgumentException("A pinned IPv4 destination and proxy credentials are required.", nameof(control));
            inbounds.Add(new { listen = "127.0.0.1", port = control.Port, protocol = "http",
                tag = ControlTag, settings = new {
                    accounts = new[] { new { user = control.User, pass = control.Password } }, allowTransparent = false
                } });
            rules.Add(new { type = "field", ruleTag = "pmt-control-vless", inboundTag = new[] { ControlTag },
                ip = new[] { target + "/32" }, port = control.DestinationPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                network = "tcp", outboundTag = "vpn" });
            rules.Add(new { type = "field", ruleTag = "pmt-control-block", inboundTag = new[] { ControlTag }, outboundTag = "block" });
            rules.Add(new { type = "field", ruleTag = "pmt-wintun-probe", inboundTag = new[] { TunTag },
                ip = new[] { target + "/32" }, port = control.DestinationPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                network = "tcp", outboundTag = "vpn" });
        }
        if (dnsOverHttps != null)
        {
            // A and AAAA use Xray's routed DoH client. Remaining record types keep the VLESS path too.
            outbounds.Add(new { tag = DnsOutbound, protocol = "dns", settings = new {
                rules = new object[] { new { action = "hijack", qType = "1,28" }, new { action = "direct" } }
            }, streamSettings = new { sockopt = new { dialerProxy = "vpn" } } });
            rules.Insert(0, new { type = "field", ruleTag = "pmt-doh-vless", inboundTag = new[] { DnsIngress }, outboundTag = "vpn" });
            rules.Add(new { type = "field", inboundTag = new[] { TunTag }, ip = new[] { resolver },
                port = "53", network = "tcp,udp", outboundTag = DnsOutbound });
        }
        rules.Add(new { type = "field", ruleTag = "pmt-tun-vless", inboundTag = new[] { TunTag }, outboundTag = "vpn" });
        object? stats = diagnosticsPort.HasValue ? new { } : null;
        object? policy = diagnosticsPort.HasValue ? new { system = new {
            statsInboundUplink = true, statsInboundDownlink = true, statsOutboundUplink = true, statsOutboundDownlink = true
        } } : null;
        return JsonSerializer.Serialize(new {
            log = new { loglevel = control != null ? "info" : "error", access = "none" },
            dns = dnsOverHttps == null ? null : new {
                tag = DnsIngress, servers = new[] { dnsOverHttps }, disableFallback = true
            },
            stats,
            policy,
            api = diagnosticsPort.HasValue ? new { tag = "pmt-tun-stats", listen = "127.0.0.1:" + diagnosticsPort.Value,
                services = new[] { "StatsService" } } : null,
            inbounds,
            outbounds,
            routing = new { domainStrategy = "AsIs", rules }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}
