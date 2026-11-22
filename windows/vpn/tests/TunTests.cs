using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Text.Json;
using Pomogator.Vpn;

internal static class NativeTunTests
{
    internal static void CheckPrivilegeGuard()
    {
        using var user = WindowsIdentity.GetCurrent();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var bytes = new byte[administrators.BinaryLength];
        administrators.GetBinaryForm(bytes, 0);
        var sid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, sid, bytes.Length);
            var deny = new SidAndAttributes { Sid = sid };
            if (!CreateRestrictedToken(user.AccessToken, 1, 1, ref deny, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (restricted)
                Require(!WindowsIdentity.RunImpersonated(restricted, TunWindows.Administrator),
                    "TUN privilege gate accepted a restricted token.");
        }
        finally { Marshal.FreeHGlobal(sid); }
    }

    private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags, uint disabledCount,
        ref SidAndAttributes disabled, uint privilegeCount, IntPtr privileges, uint restrictionCount,
        IntPtr restrictions, out SafeAccessTokenHandle result);

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
        var peer = IPAddress.Parse("203.0.113.21");
        var snapshot = TunWindows.Capture(peer);
        var fixtureRoutes = new[] { webTarget + "/32", dnsTarget + "/32" };
        var recovery = new TunRecovery(fixtureId, peer.ToString(), snapshot, fixtureRoutes, dnsTarget);
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
                var observation = await TunReadiness.Wait(() => TunWindows.Observe(recovery, web), () => tunnel.Running,
                    TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), timeLimit.Token);
                Require(observation.Ready, "Native Wintun readiness failed: " + string.Join(" | ", observation.Lines()));
                var adapter = TunWindows.Find(fixtureId);
                Require(adapter is { OperationalStatus: OperationalStatus.Up }, "Native Wintun adapter did not appear.");
                seenIndex = TunWindows.Index(adapter!);
                Require(seenIndex != beforeWeb && TunWindows.Index(web) == seenIndex &&
                    TunWindows.Index(dns) == seenIndex, "Windows did not pick the TUN routes.");
                Require(adapter!.GetIPProperties().DnsAddresses.Contains(dns), "Wintun DNS did not have the fixture address.");

                using var key = RSA.Create(2048);
                var certificateRequest = new CertificateRequest("CN=" + webTarget, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var subject = new SubjectAlternativeNameBuilder();
                subject.AddIpAddress(web);
                certificateRequest.CertificateExtensions.Add(subject.Build());
                using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddMinutes(5));
                using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
                var tcp = AnswerTcp(httpDestination, certificate, timeLimit.Token);
                using var http = TunWindows.Http(fixtureId, (_, candidate, _, _) => candidate?.GetCertHashString() == certificate.Thumbprint);
                var response = await http.GetStringAsync("https://" + webTarget + ":" + httpPort + "/fixture", timeLimit.Token);
                Require(response == "tun-ok", "Unproxied Windows HTTPS did not reach the VLESS server through the adapter.");
                await tcp;

                using var dnsFixture = CancellationTokenSource.CreateLinkedTokenSource(timeLimit.Token);
                var echoed = 0;
                var answer = AnswerUdp(udpDestination, () => Interlocked.Increment(ref echoed), dnsFixture.Token);
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(TunConfig.Address), 0));
                    var packet = new byte[] { 5, 17, 22, 90 };
                    var passed = false;
                    for (var attempt = 0; attempt < 4 && !passed; attempt++)
                    {
                        using var retry = CancellationTokenSource.CreateLinkedTokenSource(timeLimit.Token);
                        retry.CancelAfter(TimeSpan.FromSeconds(3));
                        await udp.SendAsync(packet, new IPEndPoint(dns, 53), retry.Token);
                        try { passed = (await udp.ReceiveAsync(retry.Token)).Buffer.SequenceEqual(packet); }
                        catch (OperationCanceledException) when (!timeLimit.IsCancellationRequested) { }
                    }
                    Require(passed, "No UDP reply crossed the tunnel; test packets echoed by VLESS server: " + echoed);
                    var dnsProbe = await TunConnectivity.CheckResolver(dns, seenIndex, timeLimit.Token);
                    Require(dnsProbe.Success, "Native DNS query did not pass independently of HTTPS: " + dnsProbe.Outcome);
                    try
                    {
                        using var socket = TunWindows.OpenSocket(seenIndex, IPAddress.Loopback, SocketType.Stream, ProtocolType.Tcp);
                        throw new InvalidOperationException("Probe accepted a route outside Wintun.");
                    }
                    catch (SocketException) { }
                }
                finally
                {
                    dnsFixture.Cancel();
                    try { await answer; }
                    catch (OperationCanceledException) when (dnsFixture.IsCancellationRequested) { }
                }
            }
            finally { tunnel.RequestGracefulStop(console.Signal); }
        }

        Require(await TunWindows.WaitClean(recovery, TimeSpan.FromSeconds(5)), "Normal stop left the Wintun device.");
        Require(TunWindows.Index(web) == beforeWeb && TunWindows.Index(dns) == beforeDns &&
            TunWindows.Same(snapshot, TunWindows.Capture(peer)), "Initial routes or DNS differ after stop.");
        Require(seenIndex != 0, "No TUN interface was observed.");

        using (var crashed = await Xray.StartAsync(executable, fixture, readiness, timeLimit.Token, Track,
            WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24))
        {
            child!.Kill();
            await child.WaitForExitAsync(timeLimit.Token);
        }
        if (!await TunWindows.WaitClean(recovery, TimeSpan.FromSeconds(3)))
        {
            if (TunWindows.Find(fixtureId)?.GetIPProperties().DnsAddresses.Contains(dns) == true)
                Require(!await TunWindows.Recover(recovery with { InstalledDns = "192.0.2.1" }),
                    "Recovery modified a device whose DNS did not match the saved plan.");
            string? diagnostic = null;
            var clean = await TunWindows.Recover(recovery, reason => diagnostic = reason);
            Require(clean, "Could not clean the adapter left by an abrupt exit: " + diagnostic);
        }
        Require(TunWindows.Index(web) == beforeWeb && TunWindows.Index(dns) == beforeDns &&
            TunWindows.Same(snapshot, TunWindows.Capture(peer)), "Initial routes or DNS differ after crash recovery.");
        Console.WriteLine("PASS Native Wintun: staged readiness, HTTPS and DNS through VLESS, rejected foreign route, graceful and crash restoration");
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

    private static async Task AnswerTcp(TcpListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        await using var stream = new SslStream(socket.GetStream());
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
        using var reader = new StreamReader(stream, leaveOpen: true);
        Require((await reader.ReadLineAsync(token))?.StartsWith("GET /fixture ") == true, "Unexpected fixture request.");
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\ntun-ok"u8.ToArray(), token);
    }

    private static async Task AnswerUdp(UdpClient listener, Action echoed, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var packet = await listener.ReceiveAsync(token);
            var response = packet.Buffer.Length > 12 ? DnsAnswer(packet.Buffer) : packet.Buffer;
            await listener.SendAsync(response, packet.RemoteEndPoint, token);
            echoed();
        }
    }

    internal static byte[] DnsAnswer(byte[] query, byte code = 0)
    {
        byte[] address = [0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 20, 0, 4, 198, 51, 100, 10];
        var reply = code == 0 ? query.Concat(address).ToArray() : query.ToArray();
        reply[2] = 0x81;
        reply[3] = (byte)(0x80 | code);
        reply[7] = code == 0 ? (byte)1 : (byte)0;
        return reply;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
