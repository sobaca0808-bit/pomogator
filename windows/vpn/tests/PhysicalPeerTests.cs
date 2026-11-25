using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pomogator.Vpn;

internal static class PhysicalPeerTests
{
    private sealed record Target(IPAddress Peer, string Host, TunBaseline Before, IPAddress Source,
        TunControlEndpoint Control, object Outbound);

    internal static async Task Run(string executable)
    {
        if (!TunWindows.Administrator()) throw new InvalidOperationException("Physical egress fixture requires an isolated Windows runner with admin rights.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var target = await SelectAvailable(executable, token);
        var marker = IPAddress.Parse("192.0.2.83");
        var beforeMarker = TunWindows.Index(marker);
        var routes = new[] { marker + "/32" };
        var recovery = new TunRecovery(Guid.NewGuid(), target.Peer.ToString(), target.Before, routes, marker.ToString());
        if (!TunWindows.Vacant(recovery)) throw new InvalidOperationException("Physical egress fixture's Wintun range is occupied.");
        int ready;
        do { ready = Xray.FreePort(); } while (ready == target.Control.Port);
        // The only route we add is a documentation /32. Block physical TCP to the chosen
        // peer on 443 for other applications; leave the runner's DNS and CI services reachable.
        var fixture = JsonNode.Parse(TunConfig.CreateEngine(recovery.Id, routes, [target.Peer + "/32:tcp/443"],
            target.Outbound, ready, marker.ToString(), control: target.Control, peer: target.Peer))!;
        // Neither background DNS nor any other packet entering Wintun belongs to this
        // egress-only test. Only the authenticated loopback control may use the redirect.
        fixture["routing"]!["rules"]!.AsArray().Insert(0, new JsonObject {
            ["type"] = "field", ["inboundTag"] = new JsonArray("pomogator-tun"), ["outboundTag"] = "block"
        });
        using var console = new TunConsole();
        try
        {
            var diagnostic = new TunEngineDiagnostic();
            using var tunnel = await Xray.StartAsync(executable, fixture.ToJsonString(), ready, token,
                WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24,
                diagnosticLine: diagnostic.Read, tunEgress: new TunEgress(new IPEndPoint(target.Peer, 443), target.Source, target.Before.Uplink));
            try
            {
                var observed = await TunReadiness.Wait(() => {
                    var state = TunWindows.Observe(recovery, marker);
                    return state.Ready && !diagnostic.Started ? state with { ErrorCode = "TUN_ENGINE_NOT_READY" } : state;
                }, () => tunnel.Running, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(150), token);
                if (!observed.Ready)
                    throw new InvalidOperationException("Egress fixture did not confirm its interface, DNS and routes: " + observed.Failure);
                try
                {
                    await ConnectFromTestProcess(target, token);
                    throw new InvalidOperationException("Another executable could connect to the protected peer.");
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied) { }
                await GetVerifiedAnswer(target, tunnel.ProcessId, token);
            }
            finally { tunnel.RequestGracefulStop(console.Signal); }
        }
        finally
        {
            if (!await TunWindows.WaitClean(recovery, TimeSpan.FromSeconds(5)) && !await TunWindows.Recover(recovery))
                throw new InvalidOperationException("Physical egress fixture's owned Wintun was not recovered.");
            if (TunWindows.Index(marker) != beforeMarker || !TunWindows.MatchesBaseline(target.Before, TunWindows.Capture(target.Peer)))
                throw new InvalidOperationException("Physical egress fixture did not restore its original network.");
        }
        await ConnectFromTestProcess(target, token);
        Console.WriteLine("PASS Physical IPv4 peer: fresh Xray HTTPS with the scoped WFP filter; another executable gets WinSock 10013 and regains access after stop.");
    }

    private static async Task<Target> SelectAvailable(string executable, CancellationToken token)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces().Select(adapter => adapter.GetIPProperties()).ToArray();
        var excluded = interfaces.SelectMany(properties => properties.DnsAddresses.Concat(
            properties.UnicastAddresses.Select(address => address.Address))).ToHashSet();
        var errors = new List<string>();
        foreach (var (ip, host) in new[] { ("1.1.1.1", "cloudflare-dns.com"), ("8.8.8.8", "dns.google"),
            ("1.0.0.1", "cloudflare-dns.com"), ("8.8.4.4", "dns.google") })
        {
            var peer = IPAddress.Parse(ip);
            if (excluded.Contains(peer)) continue;
            var before = TunWindows.Capture(peer);
            // Connect on UDP only asks Windows for a route and source; no datagram is sent.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(peer, 443);
            var source = ((IPEndPoint)socket.LocalEndPoint!).Address;
            var uplink = TunWindows.UplinkName(before, source);
            var control = new TunControlEndpoint(Xray.FreePort(), "fixture", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), ip, 443);
            var outbound = new { tag = "vpn", protocol = "freedom", sendThrough = source.ToString(),
                settings = new { redirect = ip + ":443" }, streamSettings = new { sockopt = new { @interface = uplink } } };
            var target = new Target(peer, host, before, source, control, outbound);
            try
            {
                await ConnectFromTestProcess(target, token);
                using var beforeTun = await Xray.StartAsync(executable, TunConfig.ReferenceEngine(control, outbound), control.Port,
                    token, WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback);
                await GetVerifiedAnswer(target, beforeTun.ProcessId, token);
                return target;
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or SocketException or OperationCanceledException)
            {
                errors.Add(host + " " + error.GetType().Name);
            }
        }
        throw new InvalidOperationException("Off-host HTTPS unavailable before TUN. Tested only public fixtures outside configured DNS: " + string.Join(", ", errors));
    }

    private static async Task ConnectFromTestProcess(Target target, CancellationToken token)
    {
        if (TunWindows.Index(target.Peer) != target.Before.Uplink)
            throw new InvalidOperationException("The physical peer route changed.");
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(target.Before.Uplink));
            socket.Bind(new IPEndPoint(target.Source, 0));
        }
        catch (SocketException error)
        {
            throw new InvalidOperationException("Could not bind the physical test socket before Connect.", error);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(target.Peer, 443, timeout.Token);
    }

    private static async Task GetVerifiedAnswer(Target target, int pid, CancellationToken token)
    {
        using var http = TunControlProbe.Client(target.Control, pid, target.Host);
        var query = TunDns.Query("example.com");
        query[0] = query[1] = 0;
        using var body = new ByteArrayContent(query);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using var response = await http.PostAsync(new Uri($"https://{target.Host}/dns-query"), body, token);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadAsByteArrayAsync(token);
        if (response.Content.Headers.ContentType?.MediaType != "application/dns-message" ||
            TunDns.Read(query, answer, out _) != TunDnsAnswer.Resolved)
            throw new HttpRequestException("Public TLS fixture did not return the expected DNS response.");
    }
}
