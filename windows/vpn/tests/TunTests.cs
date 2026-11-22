using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Text;
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
        var dnsPort = ((IPEndPoint)udpDestination.Client.LocalEndPoint!).Port;
        using var tcpDnsDestination = new TcpListener(IPAddress.Loopback, dnsPort);
        tcpDnsDestination.Start();
        using var timeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(75));

        var upstreamPort = Xray.FreePort();
        var upstreamConfig = JsonSerializer.Serialize(new {
            log = new { loglevel = "none" },
            inbounds = new[] { new { listen = "127.0.0.1", port = upstreamPort, protocol = "vless",
                settings = new { clients = new[] { new { id } }, decryption = "none" } } },
            outbounds = new object[] { new { tag = "block", protocol = "blackhole" }, Redirect("web", httpPort), Redirect("dns", dnsPort) },
            routing = new { rules = new object[] {
                new { type = "field", ip = new[] { webTarget }, port = httpPort.ToString(), network = "tcp", outboundTag = "web" },
                new { type = "field", ip = new[] { dnsTarget }, port = "53", network = "tcp,udp", outboundTag = "dns" }
            } }
        });
        using var upstream = await Xray.StartAsync(executable, upstreamConfig, upstreamPort, timeLimit.Token,
            WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback);
        var readiness = Xray.FreePort();
        var diagnosticsPort = Xray.FreePort();
        while (diagnosticsPort == readiness || diagnosticsPort == upstreamPort) diagnosticsPort = Xray.FreePort();
        var controlPort = Xray.FreePort();
        while (controlPort == readiness || controlPort == diagnosticsPort || controlPort == upstreamPort)
            controlPort = Xray.FreePort();
        var control = new TunControlEndpoint(controlPort, "tun-fixture", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            webTarget, httpPort);
        var client = Profiles.Parse($"vless://{id}@127.0.0.1:{upstreamPort}");
        // Exercise the production shape: Wintun forwards directly to the selected VLESS.
        using var console = new TunConsole();
        var fixture = TunConfig.CreateEngine(fixtureId, fixtureRoutes, new[] { "198.51.100.0/24:all", "[2001:db8:9::/48]:all" },
            XrayConfig.Outbound(client, "vpn"), readiness, dnsTarget, diagnosticsPort: diagnosticsPort, control: control);
        var seenIndex = 0;
        Process? child = null;
        IDisposable Track(Process process) { child = process; return WindowsLifetime.Attach(process); }

        using (var failed = await Xray.StartAsync(executable, fixture, readiness, timeLimit.Token, Track,
            WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24))
        {
            try
            {
                var missing = IPAddress.Parse("198.51.100.70");
                var observation = await TunReadiness.Wait(() => TunWindows.Observe(recovery, missing), () => failed.Running,
                    TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(150), timeLimit.Token);
                Require(!observation.Ready && observation.Failure!.Contains("контрольный HTTPS") &&
                    observation.AddressReady && observation.DnsReady, "Route failure did not identify the failed stage.");
            }
            finally { failed.RequestGracefulStop(console.Signal); }
        }
        Require(await TunWindows.WaitClean(recovery, TimeSpan.FromSeconds(5)) &&
            TunWindows.MatchesBaseline(snapshot, TunWindows.Capture(peer)),
            "Failed readiness did not restore the initial network before a retry.");

        using (var tunnel = await Xray.StartAsync(executable, fixture, readiness, timeLimit.Token, Track,
            WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24, diagnosticLine: line =>
            {
                // Capture only fixed markers. The test needs to distinguish local CONNECT, dispatch and VLESS.
                foreach (var marker in ControlLogMarkers)
                    if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("Xray native fixture: " + marker);
            }))
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

                // Distinct payloads exceed the MTU in both directions. Validate the actual forwarded
                // HTTP request and response after proving TLS selected the expected certificate.
                const string tlsHost = "tls-fixture.test";
                using var certificate = CreateCertificate(tlsHost);
                using var unexpected = CreateCertificate(tlsHost);
                var countersBefore = TunPacketCounters.Read(fixtureId);
                var xrayBefore = await TunXrayCounters.Read(executable, diagnosticsPort, child!.Id, timeLimit.Token);
                var requestBody = RandomNumberGenerator.GetBytes(32768);
                var responseBody = RandomNumberGenerator.GetBytes(32768);
                var tcp = AnswerHttps(httpDestination, certificate, tlsHost, requestBody, responseBody, timeLimit.Token);
                var endpoint = new Uri($"https://{tlsHost}:{httpPort}/fixture");
                using (var http = TunWindows.Http(fixtureId, (_, candidate, _, errors) =>
                    candidate?.GetCertHashString() == certificate.Thumbprint &&
                    (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0, requiredHost: tlsHost, pinnedAddress: web))
                using (var reply = await http.PostAsync(endpoint, new ByteArrayContent(requestBody), timeLimit.Token))
                {
                    reply.EnsureSuccessStatusCode();
                    Require((await reply.Content.ReadAsByteArrayAsync(timeLimit.Token)).SequenceEqual(responseBody),
                        "TLS response payload did not return through Wintun and VLESS.");
                }
                await tcp;

                var rejected = RejectHttps(httpDestination, unexpected, timeLimit.Token);
                string? observedCertificate = null;
                using (var http = TunWindows.Http(fixtureId, (_, candidate, _, _) =>
                {
                    observedCertificate = candidate?.GetCertHashString();
                    return observedCertificate == certificate.Thumbprint;
                }, requiredHost: tlsHost, pinnedAddress: web))
                {
                    try
                    {
                        using var reply = await http.GetAsync(endpoint, timeLimit.Token);
                        throw new InvalidOperationException("Unpinned HTTPS certificate was accepted.");
                    }
                    catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError)
                    {
                        Require(observedCertificate == unexpected.Thumbprint,
                            "The expected TLS failure did not receive the fixture certificate.");
                    }
                }
                await rejected;

                using var dnsFixture = CancellationTokenSource.CreateLinkedTokenSource(timeLimit.Token);
                var question = TunDns.Query(TunConnectivity.ControlName)[2..];
                var dropUdp = false;
                var dropped = 0;
                var echoed = 0;
                var answeredUdp = 0;
                var answer = AnswerUdp(udpDestination, packet =>
                {
                    if (packet.Length == 4)
                    {
                        Interlocked.Increment(ref echoed);
                        return true;
                    }
                    // Ignore Windows background DNS so unrelated requests cannot consume the HTTPS listener.
                    if (!IsQuestion(packet, question)) return false;
                    if (!Volatile.Read(ref dropUdp))
                    {
                        Interlocked.Increment(ref answeredUdp);
                        return true;
                    }
                    Interlocked.Increment(ref dropped);
                    return false;
                }, dnsFixture.Token);
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
                    Require(passed && Volatile.Read(ref echoed) > 0,
                        "No UDP payload made the round trip through Wintun and VLESS.");
                    var dnsProbe = await TunConnectivity.CheckResolver(dns, seenIndex, timeLimit.Token);
                    Require(dnsProbe.Success && Volatile.Read(ref answeredUdp) > 0,
                        "DNS request did not complete over UDP through Wintun and VLESS: " + dnsProbe.Outcome);

                    Volatile.Write(ref dropUdp, true);
                    var tcpDns = AnswerDnsTcp(tcpDnsDestination, question, dnsFixture.Token);
                    var fallback = await TunConnectivity.CheckResolver(dns, seenIndex, timeLimit.Token);
                    Require(fallback.Success && Volatile.Read(ref dropped) > 0,
                        "DNS over TCP did not recover after UDP loss through Wintun and VLESS: " + fallback.Outcome);
                    await tcpDns.WaitAsync(TimeSpan.FromSeconds(2), timeLimit.Token);

                    var countersAfter = TunPacketCounters.Read(fixtureId);
                    var xrayAfter = await TunXrayCounters.Read(executable, diagnosticsPort, child!.Id, timeLimit.Token);
                    Require(countersBefore != null && countersAfter != null &&
                        countersAfter.SentByWindows > countersBefore.SentByWindows &&
                        countersAfter.ReceivedByWindows > countersBefore.ReceivedByWindows,
                        "Windows Wintun packet counters did not confirm traffic in both directions.");
                    Require(xrayAfter != null &&
                        xrayAfter.InboundUplink - (xrayBefore?.InboundUplink ?? 0) >= requestBody.Length &&
                        xrayAfter.InboundDownlink - (xrayBefore?.InboundDownlink ?? 0) >= responseBody.Length &&
                        xrayAfter.OutboundUplink - (xrayBefore?.OutboundUplink ?? 0) >= requestBody.Length &&
                        xrayAfter.OutboundDownlink - (xrayBefore?.OutboundDownlink ?? 0) >= responseBody.Length,
                        "Xray inbound and VLESS outbound byte counters did not confirm duplex traffic.");
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

                // The same elevated Xray and VLESS serve this request with Wintun and WFP active.
                // It enters through the authenticated loopback control inlet and validates the same certificate.
                await RequireControlAuthentication(control, timeLimit.Token);
                var controlResponse = AnswerHttps(httpDestination, certificate, tlsHost, requestBody, responseBody, timeLimit.Token,
                    stage => Console.WriteLine("TLS control fixture: " + stage));
                using (var http = TunControlProbe.Client(control, child!.Id, tlsHost, (_, candidate, _, errors) =>
                    candidate?.GetCertHashString() == certificate.Thumbprint &&
                    (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0,
                    stage => Console.WriteLine("TLS control client: " + stage)))
                using (var body = new ByteArrayContent(requestBody))
                using (var reply = await ReceiveControl(http, endpoint, body, controlResponse, timeLimit.Token))
                {
                    reply.EnsureSuccessStatusCode();
                    Require((await reply.Content.ReadAsByteArrayAsync(timeLimit.Token)).SequenceEqual(responseBody),
                        "The running TUN engine's authenticated control path did not return the VLESS response.");
                }
                await controlResponse;
                await RejectControlDestination(control, "198.51.100.11", control.DestinationPort, httpDestination, timeLimit.Token);
                var otherPort = control.DestinationPort == 65535 ? 65534 : control.DestinationPort + 1;
                await RejectControlDestination(control, control.Destination, otherPort, httpDestination, timeLimit.Token);
            }
            finally { tunnel.RequestGracefulStop(console.Signal); }
        }

        Require(await TunWindows.WaitClean(recovery, TimeSpan.FromSeconds(5)), "Normal stop left the Wintun device.");
        Require(TunWindows.Index(web) == beforeWeb && TunWindows.Index(dns) == beforeDns &&
            TunWindows.MatchesBaseline(snapshot, TunWindows.Capture(peer)), "Initial routes or DNS differ after stop.");
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
            TunWindows.MatchesBaseline(snapshot, TunWindows.Capture(peer)), "Initial routes or DNS differ after crash recovery.");
        Console.WriteLine("PASS Native Wintun: TLS upload/download, authenticated and restricted same-engine VLESS control, invalid certificate, DNS UDP/TCP, route isolation and cleanup");
    }

    private static object Redirect(string tag, int port) => new { tag, protocol = "freedom", settings = new {
        redirect = "127.0.0.1:" + port,
        finalRules = new[] { new { action = "allow", ip = new[] { "127.0.0.1/32" } } }
    } };

    private static X509Certificate2 CreateCertificate(string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var subject = new SubjectAlternativeNameBuilder();
        subject.AddDnsName(host);
        request.CertificateExtensions.Add(subject.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddMinutes(10));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
    }

    private static readonly string[] ControlLogMarkers = ["request to Method [CONNECT]", "pmt-control-vless",
        "pmt-control-block", "failed to process outbound traffic", "tunneling request to", "failed to decode response header"];

    private static async Task<HttpResponseMessage> ReceiveControl(HttpClient http, Uri target, HttpContent body, Task server,
        CancellationToken token)
    {
        var response = http.PostAsync(target, body, token);
        if (await Task.WhenAny(response, server) == server) await server;
        return await response;
    }

    private static async Task AnswerHttps(TcpListener listener, X509Certificate2 certificate, string expectedSni,
        byte[] expected, byte[] response, CancellationToken token, Action<string>? observe = null)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        observe?.Invoke("server accepted connection");
        await using var stream = new SslStream(socket.GetStream());
        string? receivedSni = null;
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = (_, name) =>
            {
                receivedSni = name;
                return certificate;
            }
        }, token);
        observe?.Invoke("server completed TLS handshake");
        Require(receivedSni == expectedSni, "TLS did not send the expected DNS name as SNI for the pinned address.");

        // Read only the headers here. StreamReader can buffer bytes from the binary request body.
        var headers = new List<byte>();
        var next = new byte[1];
        while (headers.Count < 8192)
        {
            await stream.ReadExactlyAsync(next, token);
            headers.Add(next[0]);
            if (headers.Count >= 4 && headers[^4] == '\r' && headers[^3] == '\n' &&
                headers[^2] == '\r' && headers[^1] == '\n') break;
        }
        var text = Encoding.ASCII.GetString(headers.ToArray());
        Require(text.StartsWith("POST /fixture HTTP/1.", StringComparison.Ordinal) &&
            text.Contains($"\r\nContent-Length: {expected.Length}\r\n", StringComparison.OrdinalIgnoreCase),
            "Unexpected TLS fixture request headers.");
        var body = new byte[expected.Length];
        await stream.ReadExactlyAsync(body, token);
        observe?.Invoke("server received request payload");
        Require(body.SequenceEqual(expected), "Wrong TLS request payload at the VLESS egress fixture.");
        var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader, token);
        await stream.WriteAsync(response, token);
    }

    private static async Task RequireControlAuthentication(TunControlEndpoint control, CancellationToken token)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, control.Port, token);
        using var stream = client.GetStream();
        var authority = $"{control.Destination}:{control.DestinationPort}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"), token);
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Require((await reader.ReadLineAsync(token))?.StartsWith("HTTP/1.1 407 ", StringComparison.Ordinal) == true,
            "The running TUN engine's control inlet accepted a request without authentication.");
    }

    private static async Task RejectControlDestination(TunControlEndpoint control, string destination, int port,
        TcpListener upstream, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var authority = $"{destination}:{port}";
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{control.User}:{control.Password}"));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, control.Port, deadline.Token);
            using var stream = client.GetStream();
            var connect = $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Authorization: Basic {credentials}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(connect), deadline.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            // Xray acknowledges valid credentials before dispatch. The blocked route must then end
            // without relaying any application bytes or opening a connection to the VLESS destination.
            Require((await reader.ReadLineAsync(deadline.Token))?.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal) == true,
                "The routing test did not complete the authenticated CONNECT handshake.");
            string? header;
            do { header = await reader.ReadLineAsync(deadline.Token); } while (header is { Length: > 0 });
            Require(header != null && await reader.ReadAsync(new char[1], deadline.Token) == 0 &&
                !upstream.Pending(), "The control inlet relayed an authenticated request to a disallowed destination.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("An authenticated request to a disallowed destination did not close.");
        }
    }

    private static async Task RejectHttps(TcpListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        await using var stream = new SslStream(socket.GetStream());
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
            Require(await stream.ReadAsync(new byte[1], token) == 0, "Client sent HTTP bytes after rejecting TLS.");
        }
        catch (Exception error) when (error is AuthenticationException or IOException) { }
    }

    private static bool IsQuestion(byte[] packet, byte[] question) =>
        packet.Length >= 12 && packet.AsSpan(2).SequenceEqual(question);

    private static async Task AnswerDnsTcp(TcpListener listener, byte[] question, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var socket = await listener.AcceptTcpClientAsync(token);
            using var stream = socket.GetStream();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var length = new byte[2];
                await stream.ReadExactlyAsync(length, attempt.Token);
                var count = BinaryPrimitives.ReadUInt16BigEndian(length);
                if (count is < 12 or > 512) continue;
                var query = new byte[count];
                await stream.ReadExactlyAsync(query, attempt.Token);
                if (!IsQuestion(query, question)) continue;
                var reply = DnsAnswer(query);
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)reply.Length);
                await stream.WriteAsync(length, attempt.Token);
                await stream.WriteAsync(reply, attempt.Token);
                return;
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }

    private static async Task AnswerUdp(UdpClient listener, Func<byte[], bool> shouldReply, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await listener.ReceiveAsync(token); }
            // WinSock may report ICMP port-unreachable after an earlier attempt closes its socket.
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            if (!shouldReply(packet.Buffer)) continue;
            var response = packet.Buffer.Length > 12 ? DnsAnswer(packet.Buffer) : packet.Buffer;
            await listener.SendAsync(response, packet.RemoteEndPoint, token);
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
