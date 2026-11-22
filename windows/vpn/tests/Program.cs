using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Pomogator.Vpn;

const string Id = "10000000-0000-4000-8000-000000000001"; // Synthetic fixture, never a user credential.
const string Base = "vless://" + Id + "@server.example:443";
const string RealityKey = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";
int passed = 0;

Test("TLS, Cyrillic aliases and case-stable options after reload", () => {
    var profile = Profiles.Parse(Base + "?security=tls&FP=chrome&sni=vpn.example#%D0%A2%D0%B5%D1%81%D1%82");
    var restored = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!;
    Assert(restored.Name == "Тест" && restored.Option("fp") == "chrome" && restored.Transport == "raw");
});
Test("REALITY fields and loopback-only listener configuration", () => {
    var profile = Profiles.Parse(Base + "?security=reality&type=tcp&flow=xtls-rprx-vision&fp=chrome&sni=vpn.example&pbk=" + RealityKey + "&sid=01");
    using var config = JsonDocument.Parse(XrayConfig.Create(profile, 3128));
    var inbound = config.RootElement.GetProperty("inbounds")[0];
    var outbound = config.RootElement.GetProperty("outbounds");
    Assert(inbound.GetProperty("listen").GetString() == "127.0.0.1");
    Assert(outbound.GetArrayLength() == 1 && outbound[0].GetProperty("protocol").GetString() == "vless");
    var reality = outbound[0].GetProperty("streamSettings").GetProperty("realitySettings");
    Assert(reality.GetProperty("password").GetString() == RealityKey && reality.GetProperty("shortId").GetString() == "01");
});
Test("WebSocket, HTTPUpgrade, gRPC and XHTTP", () => {
    foreach (var (network, properties, expected) in new[] {
        ("ws", "&host=front.example&path=%2Fsocket", "wsSettings"),
        ("httpupgrade", "&path=%2Fedge", "httpupgradeSettings"),
        ("grpc", "&mode=multi&serviceName=example&authority=front.example", "grpcSettings"),
        ("xhttp", "&mode=stream-up&path=%2Fstream", "xhttpSettings")
    }) Assert(XrayConfig.Create(Profiles.Parse(Base + "?security=tls&type=" + network + properties), 3128).Contains(expected));
});
Test("Reject insecure and unsupported inputs without reporting secrets", () => {
    foreach (var suffix in new[] { "", "?security=tls&allowInsecure=1", "?security=tls&type=kcp",
                 "?security=reality&pbk=secret", "?security=tls&token=secret", "?security=tls&SECURITY=tls" })
        Assert(!Throws(() => Profiles.Parse(Base + suffix)).Message.Contains("secret"));
    Assert(Profiles.Parse("vless://" + Id + "@localhost:4000").Host == "127.0.0.1");
});
Test("Import VLESS lists from files and Base64 subscriptions", () => {
    var text = Base + "?security=tls#Test\n" + Base + "?security=tls#Other";
    Assert(Profiles.ParseList("\uFEFF" + text).Count == 2);
    Assert(Profiles.ParseList(Convert.ToBase64String(Encoding.UTF8.GetBytes(text))).Count == 2);
});
Test("Project only one supported VLESS outbound from a native Xray JSON", () => {
    var output = XrayConfig.Create(Profiles.Parse(Base + "?security=tls&type=ws&path=%2Fsocket"), 3128);
    var imported = Profiles.ParseList(output).Single();
    Assert(imported.Host == "server.example" && imported.Transport == "ws" && imported.Option("path") == "/socket");
    Throws(() => Profiles.ParseList(output.Replace("\"vless\"", "\"freedom\"")));
    Throws(() => Profiles.ParseList(output.Replace("\"wsSettings\":{", "\"wsSettings\":{\"dangerous\":true,")));
});
Test("Import EasyGo-style TCP REALITY with a full or simplified profile", () => {
    var json = EasyGo(@"server.example", 443, Id, RealityKey);
    var simple = Profiles.ParseList(json).Single();
    var full = Profiles.ParseList(json, keepRouting: true).Single();
    foreach (var profile in new[] { simple, full })
    {
        Assert(profile.Id == Guid.Parse(Id) && profile.Host == "server.example" && profile.Port == 443);
        Assert(profile.Security == "reality" && profile.Transport == "raw");
        Assert(profile.Option("flow") == "xtls-rprx-vision" && profile.Option("fp") == "firefox");
        Assert(profile.Option("sni") == "front.example" && profile.Option("pbk") == RealityKey && profile.Option("sid") == "0123456789abcd");
    }
    Assert(simple.Full == null && full.Full?.PrimaryTag == "proxy");
    var reopened = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(full))!;
    using var runtime = JsonDocument.Parse(XrayConfig.Create(reopened, 30001, 30002, 30003));
    var root = runtime.RootElement;
    Assert(root.GetProperty("dns").GetProperty("servers")[0].GetString() == "https://dns.example/dns-query");
    var bounds = root.GetProperty("inbounds");
    Assert(bounds.EnumerateArray().All(entry => entry.GetProperty("listen").GetString() == "127.0.0.1"));
    Assert(bounds.GetArrayLength() == 3 && bounds[0].GetProperty("tag").GetString() == "http-in");
    var rules = root.GetProperty("routing").GetProperty("rules");
    Assert(rules[0].GetProperty("inboundTag")[0].GetString() == "pomogator-vpn-check");
    Assert(rules[0].GetProperty("outboundTag").GetString() == "proxy");
    Assert(rules[1].GetProperty("outboundTag").GetString() == "direct" && rules[2].GetProperty("outboundTag").GetString() == "block");
    Assert(root.GetProperty("outbounds").EnumerateArray().Select(e => e.GetProperty("tag").GetString())
        .SequenceEqual(new[] { "proxy", "direct", "block" }));
    var incompatible = json.Replace("geoip:private", "ext:local-file:private");
    Assert(Profiles.ParseList(incompatible).Single().Full == null);
    Throws(() => Profiles.ParseList(incompatible, keepRouting: true));
    Assert((full with { Name = Id }).DisplayName == "server.example:443");
});
Test("Verify all states, guard confirmation, cancel, lose and retry", () => {
    var state = new ConnectionState();
    var phases = new List<Phase> { state.Phase };
    state.Changed += () => { if (phases[^1] != state.Phase) phases.Add(state.Phase); };
    state.Begin();
    state.Record(Check.Xray, CheckResult.Passed);
    state.Record(Check.Proxy, CheckResult.Passed);
    try { state.Confirm(DateTimeOffset.Now, false); }
    catch (InvalidOperationException) { Assert(state.Phase == Phase.Connecting); }
    Assert(state.Phase == Phase.Connecting);
    state.Record(Check.Https, CheckResult.Passed);
    state.Confirm(DateTimeOffset.Now, true);
    Assert(state.ConnectedAt != null && state.SystemProxy);
    state.ReleaseSystemProxy();
    Assert(!state.SystemProxy && state.Phase == Phase.Connected);
    state.Record(Check.Https, CheckResult.Failed);
    state.BeginStop();
    state.End(true);
    Assert(state.ConnectedAt == null && state.Result(Check.Https) == CheckResult.Failed);
    state.Begin();
    Assert(state.Result(Check.Xray) == CheckResult.Pending);
    state.BeginStop();
    state.End(false);
    Assert(phases.SequenceEqual(new[] { Phase.Disconnected, Phase.Connecting, Phase.Connected, Phase.Disconnecting,
        Phase.Error, Phase.Connecting, Phase.Disconnecting, Phase.Disconnected }));
});
await TestAsync("HTTPS subscription handles HTTP errors and safe redirects", async () => {
    var url = new Uri("https://supplier.example/sub?token=synthetic");
    var handler = new StubHttp(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/next", UriKind.Relative) } },
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Base + "?security=tls") });
    using var http = new HttpClient(handler);
    var text = await Subscriptions.DownloadAsync(http, url, CancellationToken.None);
    Assert(Profiles.ParseList(text).Count == 1 && handler.Requests[1].Query == "");
    using var forbidden = new HttpClient(new StubHttp(new HttpResponseMessage(HttpStatusCode.Forbidden)));
    Assert((await Fails(() => Subscriptions.DownloadAsync(forbidden, url, CancellationToken.None))).Message.Contains("403"));
    foreach (var target in new[] { "https://other.example/?token=synthetic", "http://supplier.example/next", "/sub?token=synthetic" })
    {
        var redirect = new StubHttp(new HttpResponseMessage(HttpStatusCode.PermanentRedirect) { Headers = { Location = new Uri(target, UriKind.RelativeOrAbsolute) } });
        using var unsafeClient = new HttpClient(redirect);
        var error = await Fails(() => Subscriptions.DownloadAsync(unsafeClient, url, CancellationToken.None));
        Assert(!error.Message.Contains("synthetic") && redirect.Requests.Count == 1);
    }
});
await TestAsync("Report an unavailable server with no credential details", async () => {
    var port = Xray.FreePort();
    var profile = Profiles.Parse($"vless://{Id}@127.0.0.1:{port}");
    var error = await Fails(() => Xray.CheckServerAsync(profile, CancellationToken.None));
    Assert(error.Message.Contains("TCP") && !error.Message.Contains(Id));
});

Test("Restore exact original proxy and retained automatic flag", () => {
    var before = new ProxyState(ProxyState.Direct | ProxyState.AutoDetect, "old:80", "<local>", "");
    var windows = new FakeProxy(before);
    var change = ProxyControl.Prepare(windows, 3500);
    ProxyControl.Apply(windows, change);
    Assert(windows.State.Server == "127.0.0.1:3500");
    Assert(ProxyControl.Restore(windows, change) == RestoreResult.Restored && windows.State == before);
    Assert(ProxyControl.Restore(windows, change) == RestoreResult.AlreadyRestored && windows.Writes == 2);
});
Test("Refuse active explicit or PAC proxy and guard against takeover", () => {
    var before = new ProxyState(ProxyState.Direct, "", "", "");
    foreach (var foreign in new[] {
        before with { Flags = ProxyState.ExplicitProxy, Server = "other:8080" },
        before with { Flags = ProxyState.AutomaticScript, Pac = "https://example.invalid" }
    }) Assert(new FakeProxy(foreign) is var windows && Throws(() => ProxyControl.Prepare(windows, 3500)) != null && windows.Writes == 0);
    var target = new FakeProxy(before);
    var change = ProxyControl.Prepare(target, 3500);
    target.State = before with { Bypass = "new" };
    Throws(() => ProxyControl.Apply(target, change));
    Assert(target.Writes == 0);
});
Test("Leave proxy changes from another program untouched", () => {
    var windows = new FakeProxy(new ProxyState(ProxyState.Direct, "", "", ""));
    var change = ProxyControl.Prepare(windows, 3500);
    ProxyControl.Apply(windows, change);
    var foreign = change.Applied with { Bypass = "externally changed" };
    windows.State = foreign;
    Assert(ProxyControl.Restore(windows, change) == RestoreResult.ChangedElsewhere && windows.State == foreign && windows.Writes == 1);
});

if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("POMOGATOR_TEST_WININET") == "1")
{
    var windows = new WinInet();
    var initial = windows.Read();
    Assert(!initial.InUse);
    var change = ProxyControl.Prepare(windows, 62003);
    // Explicit opt-in, reserved for an isolated Windows runner.
    try { ProxyControl.Apply(windows, change); }
    finally { ProxyControl.Restore(windows, change); }
    Assert(windows.Read() == initial);
    Console.WriteLine("PASS Windows WinINet settings applied and restored");
    passed++;
}

Test("TUN splits public IPv4 routes around the pinned peer and LAN", () => {
    var peer = IPAddress.Parse("203.0.113.71");
    var routes = TunConfig.Routes(peer).Select(IPNetwork.Parse).ToArray();
    Assert(!routes.Any(route => route.Contains(peer) || route.Contains(IPAddress.Parse("192.168.137.1")) ||
        route.Contains(IPAddress.Parse("127.0.0.1"))));
    Assert(routes.Any(route => route.Contains(IPAddress.Parse(TunConfig.Resolver))));
    Assert(routes.Any(route => route.Contains(IPAddress.Parse("203.0.113.70"))));
    using var config = JsonDocument.Parse(TunConfig.Engine(Guid.NewGuid(), peer, 12345, new string('a', 48), 12346));
    var settings = config.RootElement.GetProperty("inbounds")[0].GetProperty("settings");
    Assert(settings.GetProperty("autoOutboundsInterface").GetString() == "");
    Assert(settings.GetProperty("autoSystemWfpBlockLeak").EnumerateArray().Any(entry => entry.GetString() == "[::/0]:all"));
});
await TestAsync("TUN waits for a slow adapter, tentative IPv4, planned routes and delayed DNS", async () => {
    var expected = new TunObservation(Guid.NewGuid(), "fixture", Uplink: 7, ExpectedDns: TunConfig.Resolver);
    var up = expected with { Status = System.Net.NetworkInformation.OperationalStatus.Up, Index = 42 };
    var ipv4 = up with { AddressState = "Preferred", AddressReady = true };
    var routed = ipv4 with { PeerRoute = 7, ResolverRoute = 42, InternetRoute = 42 };
    var ready = routed with { Dns = new[] { TunConfig.Resolver } };
    var samples = new Queue<TunObservation>(new[] { expected, up, up with { AddressState = "Tentative" }, ipv4, routed, ready });
    var result = await TunReadiness.Wait(samples.Dequeue, () => true, TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(6), CancellationToken.None);
    Assert(samples.Count == 0 && result.Ready && result.WaitedMilliseconds > 0);
    Assert(result.Lines().Any(line => line.Contains(result.Id.ToString())));

    var wrongRoute = ready with { PeerRoute = 42 };
    Assert(wrongRoute.Failure!.Contains("к VPN-серверу"));
    var timedOut = await TunReadiness.Wait(() => wrongRoute, () => true, TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(5), CancellationToken.None);
    Assert(!timedOut.Ready && timedOut.WaitedMilliseconds >= 19);
    Assert((ready with { ResolverRoute = 7 }).Failure!.Contains("контрольный DNS"));
    Assert((ready with { InternetRoute = 7 }).Failure!.Contains("контрольный HTTPS"));
    Assert((ready with { Dns = Array.Empty<string>() }).Failure!.Contains("назначение DNS"));
    var exited = await TunReadiness.Wait(() => up, () => false, TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(10), CancellationToken.None);
    Assert(exited.ErrorCode == "TUN_XRAY_EXITED" && exited.Failure!.Contains("Ядро TUN завершилось"));
    var restarted = await TunReadiness.Wait(() => ready, () => true, TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(10), CancellationToken.None);
    Assert(restarted.Ready);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await TunReadiness.Wait(() => up, () => true, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), cancellation.Token);
        throw new InvalidOperationException("Wintun wait ignored cancellation.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
});
Test("TUN reports DNS RCODE and rejects unrelated, malformed, or empty answers", () => {
    var request = TunDns.Query(TunConnectivity.ControlName);
    var reply = NativeTunTests.DnsAnswer(request);
    Assert(TunDns.Read(request, reply, out var ok) == TunDnsAnswer.Resolved && ok == 0);
    var nxdomain = NativeTunTests.DnsAnswer(request, 3);
    Assert(TunDns.Read(request, nxdomain, out var error) == TunDnsAnswer.ServerError && error == 3);
    var truncated = request.ToArray();
    truncated[2] = 0x83;
    Assert(TunDns.Read(request, truncated, out _) == TunDnsAnswer.Truncated);
    truncated[2] = 0x81;
    Assert(TunDns.Read(request, truncated, out _) == TunDnsAnswer.NoAddress);
    Assert(TunDns.Read(request, reply.AsSpan(0, reply.Length - 1), out _) == TunDnsAnswer.Invalid);
    reply[0] ^= 1;
    Assert(TunDns.Read(request, reply, out _) == TunDnsAnswer.Invalid);
    Assert(TunDns.Read(request, NativeTunTests.DnsAnswer(TunDns.Query("example.com")), out _) == TunDnsAnswer.Invalid);
});
Test("TUN cannot report ready with failed DNS even when IPv4 HTTPS is verified", () => {
    var pass = new TunProbe(true, "fixture", 1);
    var fail = new TunProbe(false, "DNS_TIMEOUT", 3000);
    var system = new TunChecks(pass, fail, pass);
    var direct = new TunChecks(fail, pass, pass);
    Assert(!system.Success && system.FailedStage == "системное разрешение DNS");
    Assert(!direct.Success && direct.FailedStage == "запрос к DNS на Wintun");
    Assert(system.Lines().Any(line => line.Contains("DNS_TIMEOUT")));
    var state = new ConnectionState();
    state.Begin();
    foreach (var check in Enum.GetValues<Check>()) state.Record(check, CheckResult.Passed);
    state.Confirm(DateTimeOffset.Now, false);
    state.ReportHealth(system.Success, system.Https.Success);
    Assert(state.Phase == Phase.Unstable && state.Result(Check.Https) == CheckResult.Passed);
    state.ReportHealth(true, true);
    Assert(state.Phase == Phase.Connected);
});
Test("Only classified Xray diagnostics and its exit code can appear in the TUN report", () => {
    var diagnostic = new TunEngineDiagnostic();
    diagnostic.Read("fixture-secret-vless://ignored");
    Assert(diagnostic.Description == null);
    diagnostic.Read("fixture-secret unable to set routes other-credential");
    diagnostic.Exited(23);
    Assert(diagnostic.Description!.Contains("23") && diagnostic.Description.Contains("установка маршрутов") &&
        !diagnostic.Description.Contains("fixture-secret") && !diagnostic.Description.Contains("other-credential"));
});
Test("TUN detects overlay and UBoost, while PPPoE is a valid uplink", () => {
    Assert(!TunWindows.Conflicts(new[] { ("PPPoE", true, System.Net.NetworkInformation.NetworkInterfaceType.Ppp) }));
    Assert(TunWindows.Conflicts(new[] { ("UBoost", true, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) }));
    Assert(TunWindows.Conflicts(new[] { ("Custom", true, System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) }));
    var before = new TunBaseline(9, "synthetic", new[] { "192.0.2.2" }, new[] { "192.0.2.53" }, 9);
    Assert(!TunWindows.Same(before, before with { Uplink = 10 }));
    Assert(!TunWindows.Same(before, before with { Addresses = new[] { "192.0.2.3" } }));
});
Test("Crash recovery rejects a foreign route gateway, metric or destination", () => {
    var planned = new HashSet<string> { "198.51.100.53/32" };
    Assert(TunWindows.OwnRoute("198.51.100.53/32|0.0.0.0|0", planned));
    Assert(!TunWindows.OwnRoute("198.51.100.53/32|192.0.2.1|0", planned));
    Assert(!TunWindows.OwnRoute("198.51.100.53/32|0.0.0.0|15", planned));
    Assert(!TunWindows.OwnRoute("203.0.113.0/24|0.0.0.0|0", planned));
});
await TestAsync("Control host timeouts, outage round backoff and recovery", async () => {
    using var http = new HttpClient(new HealthFixture(outage: false));
    Assert(await InternetHealth.ProbeAsync(http, CancellationToken.None) != null);
    using var down = new HttpClient(new HealthFixture(outage: true));
    Assert(await InternetHealth.ProbeAsync(down, CancellationToken.None) == null);
    var policy = new HealthPolicy();
    Assert(policy.Observe(false) == TimeSpan.FromSeconds(5));
    Assert(policy.Observe(false) == TimeSpan.FromSeconds(15));
    Assert(policy.Observe(false) == null && policy.Observe(false) == null);
    var state = new ConnectionState();
    state.Begin();
    foreach (var check in Enum.GetValues<Check>()) state.Record(check, CheckResult.Passed);
    state.Confirm(DateTimeOffset.Now, false);
    state.ReportHealth(false);
    Assert(state.Active && state.Phase == Phase.Unstable && state.ConnectedAt.HasValue);
    state.ReportHealth(true);
    Assert(state.Phase == Phase.Connected && policy.Observe(true) == HealthPolicy.Regular);
});
var executable = Environment.GetEnvironmentVariable("POMOGATOR_TEST_XRAY");
if (!string.IsNullOrWhiteSpace(executable))
{
    foreach (var profile in new[] {
        Profiles.Parse(Base + "?security=tls"),
        Profiles.Parse(Base + "?security=reality&type=tcp&flow=xtls-rprx-vision&sni=vpn.example&pbk=" + RealityKey + "&sid=01"),
        Profiles.Parse(Base + "?security=tls&type=ws&path=%2Fwebsocket"),
        Profiles.Parse(Base + "?security=tls&type=httpupgrade&path=%2Fhttp"),
        Profiles.Parse(Base + "?security=tls&type=grpc&serviceName=r"),
        Profiles.Parse(Base + "?security=tls&type=xhttp&mode=stream-up&path=%2Fhttp")
    }) await CheckConfiguration(executable, profile);
    var advanced = Profiles.ParseList(EasyGo("server.example", 443, Id, RealityKey), keepRouting: true).Single();
    await CheckConfiguration(executable, advanced);
    Console.WriteLine("PASS Pinned Xray validates transports and full EasyGo configuration");
    passed++;

    await TestAsync("Classify a core startup failure separately from a proxy failure", async () => {
        var port = Xray.FreePort();
        var error = await Fails(() => Xray.StartAsync(executable, "{", port, CancellationToken.None));
        Assert(error.FailedCheck == Check.Xray);
    });

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(16));
    var destination = new TcpListener(IPAddress.Loopback, 0);
    destination.Start();
    var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
    var port = Xray.FreePort();
    var serverConfig = JsonSerializer.Serialize(new {
        log = new { loglevel = "none" },
        inbounds = new[] { new { listen = "127.0.0.1", port, protocol = "vless", settings = new {
            clients = new[] { new { id = Id } }, decryption = "none"
        } } },
        outbounds = new[] { new { protocol = "freedom", settings = new {
            finalRules = new[] { new { action = "allow", ip = new[] { "127.0.0.1/32" }, port = destinationPort.ToString() } }
        } } }
    });
    var protect = OperatingSystem.IsWindows() ? WindowsLifetime.Attach : (Func<Process, IDisposable>?)null;
    var owns = OperatingSystem.IsWindows() ? WindowsLifetime.OwnsLoopback : (Func<int, int, bool>?)null;
    using var server = await Xray.StartAsync(executable, serverConfig, port, deadline.Token, protect, owns);
    var clientProfile = Profiles.ParseList(EasyGo("127.0.0.1", port, Id, RealityKey, "none"), keepRouting: true).Single();
    await Xray.CheckServerAsync(clientProfile, deadline.Token);
    var portLocal = Xray.FreePort();
    using var probeReservation = new TcpListener(IPAddress.Loopback, 0);
    probeReservation.Start();
    var portProbe = ((IPEndPoint)probeReservation.LocalEndpoint).Port;
    var portSocks = Xray.FreePort();
    probeReservation.Stop();
    Assert(new[] { portLocal, portProbe, portSocks }.Distinct().Count() == 3);
    Process? running = null;
    IDisposable? Track(Process process) { running = process; return protect?.Invoke(process); }
    using var clientCore = await Xray.StartAsync(executable, XrayConfig.Create(clientProfile, portLocal, portProbe, portSocks),
        portLocal, deadline.Token, Track, owns);

    using var certKey = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", certKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var names = new SubjectAlternativeNameBuilder();
    names.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(names.Build());
    using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddMinutes(10));
    // Schannel requires an imported key handle for a temporary server certificate on Windows.
    using var certificate = OperatingSystem.IsWindows()
        ? new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet)
        : generated;
    try
    {
        var endpoint = new Uri($"https://127.0.0.1:{destinationPort}/fixture");
        var responder = Respond(destination, certificate, 204, deadline.Token);
        using var handler = new HttpClientHandler {
            Proxy = new WebProxy($"http://127.0.0.1:{portProbe}"),
            ServerCertificateCustomValidationCallback = (_, candidate, _, _) => candidate?.Thumbprint == certificate.Thumbprint
        };
        using var http = new HttpClient(handler);
        var verification = Xray.VerifyAsync(http, endpoint, deadline.Token);
        var completed = await Task.WhenAny(verification, responder);
        if (completed == responder) await responder;
        try { await verification; }
        catch
        {
            deadline.Cancel();
            // The fixture contains only synthetic data, so surface server handshake failures in CI.
            try { await responder; }
            catch (Exception fixtureError) when (fixtureError is not OperationCanceledException)
            { Console.Error.WriteLine(fixtureError); }
            catch (OperationCanceledException) { }
            throw;
        }
        await responder;
        var noContent = Respond(destination, certificate, 503, deadline.Token);
        var rejection = await Fails(() => Xray.VerifyAsync(http, endpoint, deadline.Token));
        Assert(rejection.Message.Contains("503"));
        await noContent;
        // Original routing blocks the regular inbound. The dedicated probe passed through the selected VLESS.
        using var ordinary = Xray.ThroughProxy(portLocal);
        ordinary.Timeout = TimeSpan.FromSeconds(2);
        await Fails(() => Xray.VerifyAsync(ordinary, endpoint, deadline.Token));

        running!.Kill();
        await running.WaitForExitAsync(deadline.Token);
        Assert(!clientCore.Running && !await clientCore.IsProxyReadyAsync(portLocal, deadline.Token));
        using var restarted = await Xray.StartAsync(executable, XrayConfig.Create(clientProfile with { Full = null }, portLocal),
            portLocal, deadline.Token, Track, owns);
        Assert(restarted.Running);
        var pid = running!.Id;
        using var tracked = Process.GetProcessById(pid);
        restarted.Dispose();
        Assert(tracked.HasExited);
    }
    finally { destination.Stop(); }
    Console.WriteLine("PASS Full profile routes probe through VLESS, rejects HTTP 503, detects process termination and restarts");
    passed++;
}
else Console.WriteLine("SKIP Core integration: set POMOGATOR_TEST_XRAY to the pinned binary.");

if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("POMOGATOR_TEST_TUN") == "1")
{
    if (string.IsNullOrEmpty(executable)) throw new InvalidOperationException("Missing pinned Xray path for TUN CI.");
    Test("Administrator guard rejects a Windows token with the Administrators SID disabled", NativeTunTests.CheckPrivilegeGuard);
    await NativeTunTests.Run(executable);
    passed++;
}
Console.WriteLine($"{passed} checks passed.");

void Test(string name, Action action)
{
    action();
    Console.WriteLine("PASS " + name);
    passed++;
}

async Task TestAsync(string name, Func<Task> action)
{
    await action();
    Console.WriteLine("PASS " + name);
    passed++;
}

static async Task<VpnError> Fails(Func<Task> action)
{
    try { await action(); }
    catch (VpnError error) { return error; }
    throw new InvalidOperationException("Expected a diagnostic failure.");
}

static string EasyGo(string host, int port, string id, string key, string security = "reality")
{
    var local = security == "none";
    object stream = local ? new { network = "tcp", security } : new {
        network = "tcp", security, tcpSettings = new { header = new { type = "none" } },
        realitySettings = new { show = false, serverName = "front.example", fingerprint = "firefox",
            publicKey = key, shortId = "0123456789abcd", spiderX = "" }
    };
    return JsonSerializer.Serialize(new {
        remarks = "Фикстура EasyGo",
        stats = new { },
        dns = new { servers = local ? new[] { "localhost" } : new[] { "https://dns.example/dns-query", "localhost" } },
        inbounds = new object[] {
            new { tag = "socks-in", listen = "0.0.0.0", port = 1080, protocol = "socks", settings = new { auth = "noauth", udp = true } },
            new { tag = "http-in", listen = "0.0.0.0", port = 8080, protocol = "http",
                sniffing = new { enabled = true, destOverride = new[] { "http", "tls" } } }
        },
        outbounds = new object[] {
            new { tag = "proxy", protocol = "vless", settings = new { vnext = new[] {
                new { address = host, port, users = new[] { new { id, encryption = "none", flow = local ? "" : "xtls-rprx-vision" } } }
            } }, streamSettings = stream, mux = new { enabled = false } },
            new { tag = "direct", protocol = "freedom", settings = new { domainStrategy = "AsIs" } },
            new { tag = "block", protocol = "blackhole", settings = new { response = new { type = "http" } } }
        },
        routing = new { domainStrategy = "AsIs", rules = new object[] {
            new { type = "field", ip = local ? new[] { "192.0.2.0/24" } : new[] { "geoip:private" }, outboundTag = "direct" },
            new { type = "field", inboundTag = new[] { "http-in" }, outboundTag = "block" }
        } }
    });
}

static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("Unexpected result.");
}

static VpnError Throws(Action action)
{
    try { action(); }
    catch (VpnError error) { return error; }
    throw new InvalidOperationException("Expected validation error.");
}

static async Task CheckConfiguration(string executable, Profile profile)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var info = new ProcessStartInfo(executable) {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(executable)!
    };
    foreach (var argument in new[] { "run", "-test", "-format", "json", "-config", "stdin:" }) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
    var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
    try
    {
        await process.StandardInput.WriteAsync(XrayConfig.Create(profile, 34000, 34001, 34002));
        process.StandardInput.Close();
        await process.WaitForExitAsync(deadline.Token);
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("Xray rejected " + profile.Security + "/" + profile.Transport + ".");
    }
    finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
}

static async Task Respond(TcpListener endpoint, X509Certificate2 cert, int status, CancellationToken token)
{
    using var socket = await endpoint.AcceptTcpClientAsync(token);
    using var ssl = new SslStream(socket.GetStream());
    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, token);
    using var reader = new StreamReader(ssl, leaveOpen: true);
    Assert((await reader.ReadLineAsync(token))?.StartsWith("GET /fixture ") == true);
    while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), token);
}

sealed class FakeProxy(ProxyState initial) : IProxySettings
{
    public ProxyState State = initial;
    public int Writes;
    public ProxyState Read() => State;
    public void Write(ProxyState state) { State = state; Writes++; }
}


sealed class StubHttp(params HttpResponseMessage[] messages) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = new(messages);
    internal readonly List<Uri> Requests = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(responses.Dequeue());
    }
}


sealed class HealthFixture(bool outage) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.Host == "www.gstatic.com")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new OperationCanceledException(token);
        }
        if (outage) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
