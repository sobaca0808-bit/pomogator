using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Pomogator.Vpn;

public static class TunConfig
{
    public const string Address = "198.18.240.1";
    public const int PrefixLength = 30;
    public const string InternetProbe = "1.1.1.1";
    public const string Resolver = "9.9.9.9";
    private const string TunTag = "pomogator-tun";

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

    public static string Bridge(Profile profile, IPAddress peer, int http, int socks, string password)
    {
        var options = new Dictionary<string, string>(profile.Options);
        if (profile.Security == "tls" && profile.Option("sni") == "") options["sni"] = profile.Host;
        var pinned = profile with { Host = peer.ToString(), Options = options, Full = null };
        return JsonSerializer.Serialize(new {
            log = new { loglevel = "none" },
            inbounds = new object[] {
                new { listen = "127.0.0.1", port = http, protocol = "http" },
                new { listen = "127.0.0.1", port = socks, protocol = "socks", settings = new {
                    auth = "password", accounts = new[] { new { user = "pomogator", pass = password } },
                    udp = true, ip = "127.0.0.1"
                } }
            },
            outbounds = new[] { XrayConfig.Outbound(pinned, "vpn") }
        });
    }

    public static string Engine(Guid id, IPAddress peer, int socks, string password, int readiness) =>
        CreateEngine(id, Routes(peer), new[] { "allexceptlan", "[::/0]:all", "misconfigtun" }, socks, password, readiness);

    // CI runs a real TUN against documentation addresses only, without any other traffic affected.
    internal static string CreateEngine(Guid id, string[] routes, string[] filters, int socks, string password, int readiness, string resolver = Resolver) =>
        JsonSerializer.Serialize(new {
            log = new { loglevel = "error" },
            inbounds = new object[] {
                new { protocol = "tun", tag = TunTag, port = 0, settings = new {
                    name = Adapter(id), desc = "PomogatorVpn", mtu = 1400,
                    gateway = new[] { Address + "/" + PrefixLength }, dns = new[] { resolver },
                    autoSystemRoutingTable = routes, autoOutboundsInterface = "",
                    autoSystemWfpBlockLeak = filters
                } },
                new { listen = "127.0.0.1", port = readiness, protocol = "http", tag = "readiness" }
            },
            outbounds = new object[] {
                new { tag = "vpn", protocol = "socks", settings = new {
                    servers = new[] { new { address = "127.0.0.1", port = socks, users = new[] { new { user = "pomogator", pass = password } } } }
                } },
                new { tag = "block", protocol = "blackhole" }
            },
            routing = new { domainStrategy = "AsIs", rules = new object[] {
                new { type = "field", inboundTag = new[] { "readiness" }, outboundTag = "block" },
                new { type = "field", ip = new[] { "::/0" }, outboundTag = "block" }
            } }
        });
}
