using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Pomogator.Vpn;

internal static class NativeTunTests
{
    internal static async Task Run(string executable)
    {
        if (!TunWindows.Administrator()) throw new InvalidOperationException("Isolated TUN test requires an administrator.");
        const string id = "10000000-0000-4000-8000-000000000001";
        var fixtureId = Guid.NewGuid();
        const string webTarget = "198.51.100.10";
        const string dnsTarget = "198.51.100.53";
        var web = IPAddress.Parse(webTarget);
        var dns = IPAddress.Parse(dnsTarget);
        var beforeWeb = TunWindows.Index(web);
        var beforeDns = TunWindows.Index(dns);
        var snapshot = TunWindows.Capture(web);
        var fixtureRoutes = new[] { webTarget + "/32", dnsTarget + "/32" };
        var recovery = new TunRecovery(fixtureId, webTarget, snapshot, fixtureRoutes, dnsTarget);
        Require(TunWindows.Vacant(recovery), "Test adapter was not initially absent.");

        using var httpDestination = new TcpListener(IPAddress.Loopback, 0);
        httpDestination.Start();
        var httpPort = ((IPEndPoint)httpDestination.LocalEndpoint).Port;
        using var udpDestination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udpDestination.Client.LocalEndPoint!).Port;
        using var timeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var upstreamPort = Xray.FreePort();
        var upstreamConfig = JsonSerializer.Serialize(new {
            log = new { loglevel = "none" },
            inbounds = new[] { new { listen = "127.0.0.1", port = upstreamPort, protocol = "vless",
                settings = new { clients = new[] { new { id } }, decryption = "none" } } },
            outbounds = new[] { Redirect("web", httpPort), Redirect("dns", udpPort) },
            routing = new { rules = new[] {
                new { type = "field", ip = new[] { dnsTarget }, outboundTag = "dns" }
            } }
        });
        using var upstream = await Xray.StartAsync(executable, upstreamConfig, upstreamPort, timeLimit.Token,
            WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback);
        var clientPort = Xray.FreePort();
        var socks = Distinct(clientPort);
        var readiness = Distinct(clientPort, socks);
        const string password = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var client = Profiles.Parse($"vless://{id}@127.0.0.1:{upstreamPort}");
        using var bridge = await Xray.StartAsync(executable, TunConfig.Bridge(client, IPAddress.Loopback, clientPort, socks, password),
            clientPort, timeLimit.Token, WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback);

        // This test is intentionally not a default-route test. The runner's connection and DNS are left on its original interface.
        using var console = new TunConsole();
        var fixture = TunConfig.CreateEngine(fixtureId, fixtureRoutes,
            new[] { "[2001:db8:9::/48]:all" }, socks, password, readiness, dnsTarget);
        var seenIndex = 0;
        Process? child = null;
        IDisposable Track(Process process) { child = process; return WindowsLifetime.Attach(process); }

        using (var tunnel = await Xray.StartAsync(executable, fixture, readiness, timeLimit.Token, Track,
            WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24))
        {
            try
            {
                var adapter = TunWindows.Find(fixtureId);
                Require(adapter is { OperationalStatus: OperationalStatus.Up }, "Native Wintun adapter did not appear.");
                seenIndex = TunWindows.Index(adapter!);
                Require(seenIndex != beforeWeb && TunWindows.Index(web) == seenIndex &&
                    TunWindows.Index(dns) == seenIndex, "Windows did not pick the TUN routes.");
                Require(adapter!.GetIPProperties().DnsAddresses.Contains(dns), "Wintun DNS did not have the fixture address.");

                var tcp = AnswerTcp(httpDestination, timeLimit.Token);
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
                var response = await http.GetStringAsync("http://" + webTarget + ":" + httpPort + "/fixture", timeLimit.Token);
                Require(response == "tun-ok", "Unproxied Windows HTTP did not reach the VLESS server through the adapter.");
                await tcp;

                var answer = AnswerUdp(udpDestination, timeLimit.Token);
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                var packet = new byte[] { 5, 17, 22, 90 };
                await udp.SendAsync(packet, new IPEndPoint(dns, 53), timeLimit.Token);
                var reply = await udp.ReceiveAsync(timeLimit.Token);
                Require(reply.Buffer.SequenceEqual(packet), "UDP on the fixture DNS address did not cross the tunnel.");
                await answer;
            }
            finally { tunnel.RequestGracefulStop(console.Signal); }
        }

        Require(await TunWindows.WaitClean(recovery, TimeSpan.FromSeconds(5)), "Normal stop left the Wintun device.");
        Require(TunWindows.Index(web) == beforeWeb && TunWindows.Index(dns) == beforeDns &&
            TunWindows.Same(snapshot, TunWindows.Capture(web)), "Initial routes or DNS differ after stop.");
        Require(seenIndex != 0, "No TUN interface was observed.");

        using (var crashed = await Xray.StartAsync(executable, fixture, readiness, timeLimit.Token, Track,
            WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24))
        {
            child!.Kill();
            await child.WaitForExitAsync(timeLimit.Token);
        }
        if (!await TunWindows.WaitClean(recovery, TimeSpan.FromSeconds(3)))
        {
            Require(!await TunWindows.Recover(recovery with { InstalledDns = "192.0.2.1" }),
                "Recovery modified a device whose DNS did not match the saved plan.");
            string? diagnostic = null;
            var clean = await TunWindows.Recover(recovery, reason => diagnostic = reason);
            Require(clean, "Could not clean the adapter left by an abrupt exit: " + diagnostic);
        }
        Require(TunWindows.Index(web) == beforeWeb && TunWindows.Index(dns) == beforeDns &&
            TunWindows.Same(snapshot, TunWindows.Capture(web)), "Initial routes or DNS differ after crash recovery.");
        Console.WriteLine("PASS Native Wintun: Windows HTTP (UseProxy=false), UDP, DNS/route scope, graceful restoration and process-crash recovery");
    }

    private static object Redirect(string tag, int port) => new { tag, protocol = "freedom", settings = new {
        redirect = "127.0.0.1:" + port,
        finalRules = new[] { new { action = "allow", ip = new[] { "127.0.0.1/32" } } }
    } };

    private static int Distinct(params int[] used)
    {
        int port;
        do { port = Xray.FreePort(); } while (used.Contains(port));
        return port;
    }

    private static async Task AnswerTcp(TcpListener listener, CancellationToken token)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        Require((await reader.ReadLineAsync(token))?.StartsWith("GET /fixture ") == true, "Unexpected fixture request.");
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\ntun-ok"u8.ToArray(), token);
    }

    private static async Task AnswerUdp(UdpClient listener, CancellationToken token)
    {
        var packet = await listener.ReceiveAsync(token);
        await listener.SendAsync(packet.Buffer, packet.RemoteEndPoint, token);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
