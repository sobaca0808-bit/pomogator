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

await TestAsync("Choose a verified IPv4 after HTTPS failure; exclude duplicates and reserved addresses", async () => {
    var first = IPAddress.Parse("192.0.2.11");
    var second = IPAddress.Parse("192.0.2.12");
    var seen = new List<IPAddress>();
    var selected = await TunPeerSelection.TryCandidates(
        new[] { IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse(TunConfig.Resolver), first, first, second },
        (ip, token) => {
            seen.Add(ip);
            if (ip.Equals(first)) throw new VpnError("fixture: TLS failure", Check.Https);
            return Task.FromResult(ip);
        }, _ => { }, CancellationToken.None);
    Assert(selected.Equals(second) && seen.SequenceEqual(new[] { first, second }));
});
await TestAsync("Limit endpoint attempts, preserve failure stage and propagate user cancellation", async () => {
    var peers = Enumerable.Range(1, 6).Select(i => IPAddress.Parse($"192.0.2.{i}"));
    var calls = 0;
    try {
        await TunPeerSelection.TryCandidates<int>(peers, (_, _) => { calls++; throw new VpnError("fixture", Check.Https); },
            _ => { }, CancellationToken.None);
        throw new Exception("Missing exhaustion error");
    } catch (NoVerifiedPeer error) { Assert(calls == 3 && error.FailedCheck == Check.Https); }
    using var cancel = new CancellationTokenSource();
    try {
        await TunPeerSelection.TryCandidates<int>(peers, (_, token) => {
            calls++; cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(1);
        }, _ => { }, cancel.Token);
        throw new Exception("Cancellation ignored");
    } catch (OperationCanceledException) { Assert(calls == 4); }
});
await TestAsync("HTTP/3 source selection does not require a TCP listener", async () => {
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    var profile = Profiles.Parse($"vless://{Id}@127.0.0.1:{port}?security=tls&type=xhttp&alpn=h3&sni=fixture.test");
    var route = await Xray.CheckServerSourceAsync(profile, CancellationToken.None);
    Assert(route.Source.Equals(IPAddress.Loopback));
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
    Assert(RasProxyScope.Active() == null); // CI has no dial-up connections.
    var windows = new WinInet();
    var initial = windows.Read();
    Assert(!initial.InUse);
    var change = ProxyControl.Prepare(windows, 62003);
    // Explicit opt-in, reserved for an isolated Windows runner.
    try
    {
        ProxyControl.Apply(windows, change);
        var current = ProxyControl.VerifiedWindowsProxy(WinInet.ReadEffective(), 62003);
        Assert(current.GetProxy(new Uri("https://example.org/")) == new Uri("http://127.0.0.1:62003/"));
    }
    finally { ProxyControl.Restore(windows, change); }
    Assert(windows.Read() == initial);
    Console.WriteLine("PASS Windows WinINet settings applied and restored");
    passed++;
}

Test("Use the active Windows HTTPS proxy and keep its bypass rules", () => {
    var route = new Uri("https://example.org/");
    var effective = new ProxyState(ProxyState.Direct | ProxyState.ExplicitProxy,
        "http=other:80;https=127.0.0.1:3500", "<local>;*.skip.test", "");
    var proxy = ProxyControl.VerifiedWindowsProxy(effective, 3500);
    Assert(proxy.GetProxy(route) == new Uri("http://127.0.0.1:3500/"));
    Assert(proxy.IsBypassed(new Uri("https://abc.skip.test/")) && proxy.IsBypassed(new Uri("https://fileserver/")));
    foreach (var rejected in new[] {
        effective with { Server = "127.0.0.1:3501" },
        effective with { Server = "http=127.0.0.1:3500" },
        effective with { Flags = effective.Flags | ProxyState.AutoDetect },
        effective with { Pac = "https://example.invalid/proxy.pac" }
    }) Throws(() => ProxyControl.VerifiedWindowsProxy(rejected, 3500));
});
Test("Select a single active RAS scope and refuse ambiguous proxy changes", () => {
    Assert(RasProxyScope.Select(Array.Empty<(string, string)>()) == null);
    Assert(RasProxyScope.Select(new[] { ("Broadband", "PPPoE"), ("broadband", "pppoe") }) == "Broadband");
    Throws(() => RasProxyScope.Select(new[] { ("Broadband", "PPPoE"), ("Another connection", "PPPoE") }));
    Throws(() => RasProxyScope.Select(new[] { ("Other VPN", "vpn") }));
});
await TestAsync("Xray chooses a fresh port with bounded retries and never retries a profile error", async () => {
    var plans = 0;
    var failure = await Fails(() => Xray.StartWithPortsAsync("", () => ("{}", ++plans, Array.Empty<int>()),
        CancellationToken.None, _ => null, (_, _) => false,
        launch: (_, _, _) => throw new VpnError("busy", Check.Proxy)));
    Assert(plans == 3 && failure.FailedCheck == Check.Proxy);
    plans = 0;
    await Fails(() => Xray.StartWithPortsAsync("", () => ("{}", ++plans, Array.Empty<int>()),
        CancellationToken.None, _ => null, (_, _) => false,
        launch: (_, _, _) => throw new VpnError("profile", Check.Xray)));
    Assert(plans == 1);
});
Test("TUN splits public IPv4 routes around the pinned peer and LAN", () => {
    var peer = IPAddress.Parse("203.0.113.71");
    var routes = TunConfig.Routes(peer).Select(IPNetwork.Parse).ToArray();
    Assert(!routes.Any(route => route.Contains(peer) || route.Contains(IPAddress.Parse("192.168.137.1")) ||
        route.Contains(IPAddress.Parse("127.0.0.1"))));
    Assert(routes.Any(route => route.Contains(IPAddress.Parse(TunConfig.Resolver))));
    Assert(routes.Any(route => route.Contains(IPAddress.Parse("203.0.113.70"))));
    using var config = JsonDocument.Parse(TunConfig.Engine(Guid.NewGuid(), peer, IPAddress.Parse("192.0.2.31"), "Fixture uplink", Profiles.Parse(Base + "?security=tls"), 12346));
    var settings = config.RootElement.GetProperty("inbounds")[0].GetProperty("settings");
    Assert(settings.GetProperty("autoOutboundsInterface").GetString() == "");
    Assert(config.RootElement.GetProperty("outbounds")[0].GetProperty("protocol").GetString() == "vless");
    Assert(config.RootElement.GetProperty("outbounds")[0].GetProperty("settings").GetProperty("vnext")[0]
        .GetProperty("address").GetString() == peer.ToString());
    Assert(settings.GetProperty("autoSystemWfpBlockLeak").EnumerateArray().Any(entry => entry.GetString() == "[::/0]:all"));
});
Test("TUN reports the failing source or WFP setup without retaining raw engine details", () => {
    var diagnostic = new TunEngineDiagnostic();
    diagnostic.Read("TUN source IPv4 is not preferred on the uplink: fixture-private-data");
    Assert(diagnostic.Description!.Contains("исходного IPv4") && !diagnostic.Description.Contains("fixture-private-data"));
    diagnostic.Read("FwpmFilterAdd0 failed: fixture-private-data");
    Assert(diagnostic.Description!.Contains("WFP") && !diagnostic.Description.Contains("fixture-private-data"));
});
Test("The TUN helper sets the child transport scope and proxy launches discard inherited scope", () => {
    var info = new ProcessStartInfo { UseShellExecute = false };
    const string peer = "POMOGATOR_TUN_PEER", source = "POMOGATOR_TUN_SOURCE", index = "POMOGATOR_TUN_IFINDEX";
    var egress = new TunEgress(new IPEndPoint(IPAddress.Parse("203.0.113.57"), 8443), IPAddress.Parse("192.0.2.9"), 371);
    TunEgress.Configure(info, egress);
    Assert(info.Environment[peer] == "203.0.113.57:8443" && info.Environment[source] == "192.0.2.9" && info.Environment[index] == "371");
    const string protocol = "POMOGATOR_TUN_PROTOCOL";
    Assert(info.Environment[protocol] == "tcp");
    foreach (var (options, expected) in new[] { ("tls&type=xhttp&alpn=h3", "udp"), ("tls&type=xhttp&alpn=h3,h2", "tcp"),
        ("tls&type=tcp&alpn=h3", "tcp"), ("tls&type=xhttp", "tcp") })
    {
        var profile = Profiles.Parse(Base + "?security=" + options);
        TunEgress.Configure(info, TunEgress.ForProfile(egress.Peer.Address, egress.Source, egress.InterfaceIndex, profile));
        Assert(info.Environment[protocol] == expected);
    }
    TunEgress.Configure(info, null);
    Assert(!info.Environment.ContainsKey(protocol) && !info.Environment.ContainsKey(peer) && !info.Environment.ContainsKey(source) && !info.Environment.ContainsKey(index));
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
Test("Production TUN routes encrypted DNS and keeps telemetry on loopback", () => {
    using var config = JsonDocument.Parse(TunConfig.Engine(Guid.NewGuid(), IPAddress.Parse("203.0.113.71"), IPAddress.Parse("192.0.2.31"), "Fixture uplink",
        Profiles.Parse(Base + "?security=tls"), 34002, 34003));
    var root = config.RootElement;
    Assert(root.GetProperty("dns").GetProperty("servers")[0].GetString() == "https://dns.quad9.net/dns-query");
    Assert(root.GetProperty("dns").GetProperty("disableFallback").GetBoolean());
    Assert(root.GetProperty("api").GetProperty("listen").GetString() == "127.0.0.1:34003");
    Assert(root.GetProperty("api").GetProperty("services")[0].GetString() == "StatsService");
    var rules = root.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
    Assert(rules[0].GetProperty("inboundTag")[0].GetString() == root.GetProperty("dns").GetProperty("tag").GetString());
    Assert(rules[0].GetProperty("outboundTag").GetString() == "vpn");
    Assert(rules.Any(rule => rule.TryGetProperty("port", out var port) && port.GetString() == "53" &&
        rule.GetProperty("network").GetString() == "tcp,udp"));
    var dns = root.GetProperty("outbounds").EnumerateArray().Single(item => item.GetProperty("protocol").GetString() == "dns");
    Assert(dns.GetProperty("streamSettings").GetProperty("sockopt").GetProperty("dialerProxy").GetString() == "vpn");
});
Test("Bind TUN VLESS to the selected interface and reject the VPN peer re-entering Wintun", () => {
    var peer = IPAddress.Parse("203.0.113.71");
    var source = IPAddress.Parse("192.0.2.31");
    var profile = Profiles.Parse(Base + "?security=reality&type=tcp&flow=xtls-rprx-vision&sni=vpn.example&pbk=" + RealityKey + "&sid=01");
    using var config = JsonDocument.Parse(TunConfig.Engine(Guid.NewGuid(), peer, source, "Fixture uplink", profile, 34002));
    var root = config.RootElement;
    var outbound = root.GetProperty("outbounds")[0];
    using var preflight = JsonDocument.Parse(TunConfig.PreflightEngine(peer, source, "Fixture uplink", profile, 34006));
    Assert(preflight.RootElement.GetProperty("outbounds")[0].GetRawText() == outbound.GetRawText());
    Assert(outbound.GetProperty("sendThrough").GetString() == source.ToString());
    var stream = outbound.GetProperty("streamSettings");
    Assert(stream.GetProperty("sockopt").GetProperty("interface").GetString() == "Fixture uplink");
    Assert(stream.GetProperty("realitySettings").GetProperty("serverName").GetString() == "vpn.example");
    try { TunConfig.Engine(Guid.NewGuid(), peer, source, "", profile, 34002); throw new InvalidOperationException("No interface guard."); }
    catch (ArgumentException error) { Assert(error.ParamName == "uplink"); }
    var rules = root.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
    var rejected = Array.FindIndex(rules, rule => rule.TryGetProperty("ruleTag", out var tag) &&
        tag.GetString() == "pmt-peer-reentry");
    Assert(rejected >= 0 && rejected < rules.Length - 1);
    var guard = rules[rejected];
    Assert(guard.GetProperty("inboundTag")[0].GetString() == "pomogator-tun" &&
        guard.GetProperty("ip")[0].GetString() == peer + "/32" && guard.GetProperty("outboundTag").GetString() == "block");
    using var proxy = JsonDocument.Parse(XrayConfig.Create(profile, 34003));
    Assert(!proxy.RootElement.GetProperty("outbounds")[0].TryGetProperty("sendThrough", out _));
    var baseline = new TunBaseline(7, "fixture", new[] { source.ToString(), TunConfig.Address }, [], 7);
    Assert(baseline.HasSource(source) && !baseline.HasSource(peer) && !baseline.HasSource(IPAddress.Parse(TunConfig.Address)));
});
Test("Reference process keeps Wintun absent and routes only one authenticated IPv4 probe", () => {
    var endpoint = new TunControlEndpoint(34004, "fixture-user", "fixture-password", "1.1.1.1", 443);
    using var json = JsonDocument.Parse(TunConfig.ReferenceEngine(endpoint, new { tag = "vpn", protocol = "vless" }));
    var inbound = json.RootElement.GetProperty("inbounds").EnumerateArray().Single();
    Assert(inbound.GetProperty("listen").GetString() == "127.0.0.1" && inbound.GetProperty("protocol").GetString() == "http");
    Assert(inbound.GetProperty("settings").GetProperty("accounts").GetArrayLength() == 1);
    var rules = json.RootElement.GetProperty("routing").GetProperty("rules");
    Assert(rules.GetArrayLength() == 2 && rules[0].GetProperty("ip")[0].GetString() == "1.1.1.1/32" &&
        rules[0].GetProperty("port").GetString() == "443" && rules[1].GetProperty("outboundTag").GetString() == "block");
});
Test("Distinguish combined-engine, elevated-context and common failures", () => {
    var before = new TunProbe(true, "fixture", 12);
    var failure = new TunProbe(false, "fixture", 14);
    Assert(TunConnectivity.CompareReferences(new(before, before), failure).Contains("сам обслуживает TUN"));
    Assert(TunConnectivity.CompareReferences(new(before, failure), before).Contains("контекста процесса"));
    Assert(TunConnectivity.CompareReferences(new(before, failure), failure).Contains("оба контрольных процесса"));
    Assert(TunConnectivity.CompareReferences(new(failure), before).Contains("не подтвердил пару замеров"));
});
Test("Only the authenticated diagnostic inlet can reach the pinned destination", () => {
    var endpoint = new TunControlEndpoint(34004, "fixture-user", "fixture-password", "1.1.1.1", 443);
    using var config = JsonDocument.Parse(TunConfig.Engine(Guid.NewGuid(), IPAddress.Parse("203.0.113.71"), IPAddress.Parse("192.0.2.31"), "Fixture uplink",
        Profiles.Parse(Base + "?security=tls"), 34002, 34003, endpoint));
    var root = config.RootElement;
    var inlet = root.GetProperty("inbounds").EnumerateArray().Single(item =>
        item.GetProperty("tag").GetString() == "pmt-tun-control");
    Assert(inlet.GetProperty("listen").GetString() == "127.0.0.1");
    Assert(inlet.GetProperty("settings").GetProperty("accounts")[0].GetProperty("pass").GetString() == endpoint.Password);
    var rules = root.GetProperty("routing").GetProperty("rules").EnumerateArray().Where(rule =>
        rule.TryGetProperty("ruleTag", out var tag) && tag.GetString()!.StartsWith("pmt-control-")).ToArray();
    Assert(rules.Length == 2 && rules[0].GetProperty("ip")[0].GetString() == "1.1.1.1/32" &&
        rules[0].GetProperty("port").GetString() == "443" && rules[0].GetProperty("network").GetString() == "tcp" &&
        rules[0].GetProperty("outboundTag").GetString() == "vpn" && rules[1].GetProperty("outboundTag").GetString() == "block");
    Assert(root.GetProperty("log").GetProperty("access").GetString() == "none");
});
Test("Differential diagnosis and Xray event sampling export categories without raw secrets", () => {
    var engine = new TunEngineDiagnostic();
    Assert(!engine.Started);
    engine.Read("2026/11/22 [Warning] core: Xray 26.11.9 started");
    Assert(engine.Started);
    engine.Read("Hit route rule: [pmt-control-vless] marker");
    engine.Read("transport/internet/tcp: failed to dial to dest marker");
    engine.Read("proxy/vless/outbound: failed to transfer response payload > no route to host");
    engine.Read("app/proxyman/outbound: failed to process outbound traffic > REALITY: received real certificate marker");
    engine.Read("failed to handle DOH response marker");
    engine.Read("failed to decode response header marker");
    engine.Read("Hit route rule: [pmt-peer-reentry] marker");
    engine.Read("proxy/vless/outbound: tunneling request to marker");
    var events = engine.TakeEvents();
    Assert(events.Length == 8 && events.Single(line => line.Contains("сбой открытия исходящего сокета")).EndsWith("× 1") &&
        !string.Join(" ", events).Contains("marker") && engine.TakeEvents().Length == 0);
    var fail = new TunProbe(false, "пример ошибки", 3);
    var pass = new TunProbe(true, "пример успеха", 5);
    var report = new TunChecks(pass, pass, fail, events, fail, pass);
    Assert(!report.Success);
    Assert(report.Lines().Any(line => line.Contains("Сбой наблюдается на пути через Wintun")));
    Assert(report.Lines().Any(line => line.Contains("HTTPS через локальный вход того же Xray")));
    Assert(new TunChecks(fail, fail, fail, DirectPinned: fail, SameEngine: fail).FailedStage.Contains("VLESS"));
    Assert(new TunChecks(fail, fail, pass, DirectPinned: pass, SameEngine: pass).FailedStage.Contains("DNS"));
    Assert(TunConnectivity.PeerLine(PeerTcp.Seen | PeerTcp.ExpectedSource | PeerTcp.Established, 7, 7).Contains("ESTABLISHED"));
    Assert(TunConnectivity.PeerLine(PeerTcp.None, 7, 7).Contains("сокет не зафиксирован"));
});
Test("Correlate known socket destinations and separate raw TCP, TLS and VLESS failure", () => {
    var events = new TunSocketDiagnostic(IPAddress.Parse("203.0.113.71"), 443, rawTransport: true);
    events.Read("[Info] [192] app/dispatcher: Hit route rule: [pmt-control-vless] so taking detour [vpn] for [tcp:1.1.1.1:443]");
    events.Read("[Info] [192] transport/internet/tcp: dialing TCP to tcp:203.0.113.71:443");
    events.Read("[Info] [193] app/dispatcher: Hit route rule: [pmt-wintun-probe] so taking detour [vpn] for [tcp:1.1.1.1:443]");
    events.Read("[Info] [192] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to find an available destination > [transport/internet/tcp: failed to dial to dest > dial tcp 192.0.2.31:0->203.0.113.71:443: connectex: WinSock 10061]");
    events.Read("[Info] [193] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to find an available destination > [read tcp 192.0.2.31:35158->203.0.113.8:1443: wsarecv: winapi error #10054]");
    events.Read("[Info] app/dispatcher: Hit route rule: [pmt-doh-vless] so taking detour [vpn] for [tcp:dns.quad9.net:443]");
    events.Read("[Info] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to decode response header > EOF");
    events.Read("[Error] app/dns: failed to retrieve response for fixture-secret");
    var report = string.Join("\n", events.TakeEvents());
    Assert(report.Contains("контроль через Xray → IP:порт VPN-сервера · подключение TCP (ConnectEx) · WinSock 10061"));
    Assert(report.Contains("Wintun к цели HTTPS → другой IP:порт · ввод-вывод после TCP") && report.Contains("WinSock 10054"));
    Assert(report.Contains("поток без метки → назначение не подтверждено · передача VLESS · код WinSock не передан"));
    Assert(report.Contains("резолвер DoH") && !report.Contains("fixture-secret") && !report.Contains("203.0.113."));
    Assert(events.TakeEvents().Length == 0);
    events.Read("[Info] [192] proxy/vless/outbound: tunneling request to tcp:1.1.1.1:443 via 203.0.113.71:443");
    Assert(events.TakeEvents().Single().Contains("контроль через Xray → IP:порт VPN-сервера · внешний транспорт открыт"));
});
Test("Report Windows packets separately from Xray bytes and preserve unknown/reset", () => {
    const string json = """
        {"stat":[{"name":"inbound>>>pomogator-tun>>>traffic>>>uplink","value":"31"},
        {"name":"inbound>>>pomogator-tun>>>traffic>>>downlink"},
        {"name":"outbound>>>vpn>>>traffic>>>uplink","value":70},
        {"name":"outbound>>>vpn>>>traffic>>>downlink","value":"4"},
        {"name":"unrelated","value":833}]}
        """;
    var parsed = TunXrayCounters.Parse(json);
    Assert(parsed == new TunXraySample(31, 0, 70, 4));
    Assert(TunXrayCounters.Parse("""{"stat":[]}""") == null);
    var before = new TunTrafficSample(new TunPacketSample(9, 10, 5, 2, 0, 0), new TunXraySample(3, 0, 4, 0));
    var after = new TunTrafficSample(new TunPacketSample(11, 10, 4, 2, 0, 0), parsed);
    var lines = after.Describe("DNS", before).ToArray();
    Assert(lines.Any(line => line.Contains("Windows → Wintun: 2") && line.Contains("н/д/0")));
    Assert(lines.Any(line => line.Contains("байты полезной нагрузки Xray TUN ↑/↓: 28/0")));
    Assert(lines.Any(line => line.Contains("возврат полезной нагрузки")));
});
Test("Link a WFP block only to an unambiguous ConnectEx tuple from the denied server flow", () => {
    var peer = IPAddress.Parse("203.0.113.71");
    var local = TunWfpDiagnostic.Address(IPAddress.Parse("192.0.2.31"));
    var diagnostic = new TunSocketDiagnostic(peer, 443, rawTransport: true);
    diagnostic.Read("[Info] [192] transport/internet/tcp: dialing TCP to tcp:203.0.113.71:443");
    diagnostic.Read("[Info] [192] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to find an available destination > common/retry: [transport/internet/tcp: failed to dial to dest > dial tcp 192.0.2.31:38158->203.0.113.71:443: connectex: winapi error #10013 transport/internet/tcp: failed to dial to dest > dial tcp 192.0.2.31:38159->203.0.113.8:443: connectex: winapi error #10013]");
    diagnostic.TakeEvents(out var denials);
    Assert(denials.Length == 1 && denials[0].FlowId == 192 && denials[0].LocalPort == 38158 && denials[0].LocalAddress == local);
    var attempt = denials[0];
    var drop = new TunWfpDrop(attempt.Until, local, 38158, 512, 48, true, "TEST WFP");
    var exact = TunWfpDiagnostic.Correlate(denials, [drop], local).Single();
    Assert(exact.Contains("ID потока Xray 192") && exact.Contains("фильтр 512") && exact.Contains("ожидаемый IPv4") &&
        !exact.Contains("192.0.2.") && !exact.Contains("203.0.113."));
    Assert(TunWfpDiagnostic.Correlate([attempt with { LocalPort = 0 }], [drop]).Single().Contains("конкретный поток не установлен"));
    Assert(TunWfpDiagnostic.Correlate(denials, [drop with { ConnectLayer = false }]).Single().Contains("конкретный поток не установлен"));
    Assert(TunWfpDiagnostic.Correlate([attempt, attempt with { FlowId = 193 }], [drop]).Single().Contains("конкретный поток не установлен"));
    Assert(TunWfpDiagnostic.Correlate(denials, [drop with { At = drop.At.AddMinutes(-1) }]).Single().Contains("не найдено"));
    diagnostic.Read("[Info] [194] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to find an available destination > transport/internet/tcp: failed to dial to dest > dial tcp 192.0.2.31:0->203.0.113.71:443: connectex: winapi error #10013");
    diagnostic.TakeEvents(out denials);
    Assert(denials.Single().LocalPort == 0);
});
Test("WFP counts peer drops with missing or different images separately from Xray", () => {
    var now = DateTimeOffset.UtcNow;
    var source = TunWfpDiagnostic.Address(IPAddress.Parse("192.0.2.16"));
    var attempt = new TunDialDenial(7419, source, 49521, now.AddSeconds(-1), now);
    var uncertain = new TunWfpUnattributedDrop[] {
        new(now, OtherImage: false), new(now, OtherImage: true), new(now.AddMilliseconds(-200), OtherImage: true),
        new(now.AddSeconds(-2), OtherImage: false), new(now.AddSeconds(2), OtherImage: true)
    };
    var report = TunWfpDiagnostic.Correlate([attempt], [], source, capped: true, unattributed: uncertain);
    Assert(report.Any(line => line.Contains("для образа Xray") && line.Contains("не найдено")));
    var counts = report.Single(line => line.Contains("другой образ"));
    Assert(counts.Contains("образ не определён: 1") && counts.Contains("другой образ: 2") &&
        counts.Contains("не установлена") && !report.Any(line => line.Contains("7419") || line.Contains("49521") ||
        line.Contains("192.0.2.16")) && report.Any(line => line.Contains("512")));
    var confirmed = new TunWfpDrop(now, source, 49521, 31, 48, true, "TEST WFP");
    var mixed = TunWfpDiagnostic.Correlate([attempt], [confirmed], source, unattributed: uncertain);
    Assert(mixed.Length == 2 && mixed.Single(line => line.Contains("7419")).Contains("фильтр 31") && mixed.Contains(counts));
    Assert(TunWfpDiagnostic.Correlate([attempt], [], unattributed: uncertain.Skip(3)).Single().Contains("не найдено"));
});
Test("TUN detects overlay and UBoost, while PPPoE is a valid uplink", () => {
    Assert(!TunWindows.Conflicts(new[] { ("PPPoE", true, System.Net.NetworkInformation.NetworkInterfaceType.Ppp) }));
    Assert(TunWindows.Conflicts(new[] { ("UBoost", true, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) }));
    Assert(TunWindows.Conflicts(new[] { ("Custom", true, System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) }));
    var before = new TunBaseline(9, "synthetic", new[] { "192.0.2.2" }, new[] { "192.0.2.53" }, 9);
    Assert(!TunWindows.Same(before, before with { Uplink = 10 }));
    Assert(!TunWindows.Same(before, before with { Addresses = new[] { "192.0.2.3" } }));
    Assert(TunWindows.MatchesBaseline(before, before with { PublicRoute = 9 })); // Older DPAPI records omit PublicRoute.
    Assert(!TunWindows.MatchesBaseline(before with { PublicRoute = 9 }, before with { PublicRoute = 42 }));
    Assert(!TunWindows.MatchesBaseline(before, before with { InternetRoute = 42 }));
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
    var tunConfig = TunConfig.Engine(Guid.NewGuid(), IPAddress.Parse("203.0.113.71"), IPAddress.Parse("192.0.2.31"), "Fixture uplink", advanced, 34010, 34011,
        new TunControlEndpoint(34012, "fixture-user", "fixture-password", TunConfig.InternetProbe, 443));
    await CheckConfiguration(executable, advanced, tunConfig);
    Console.WriteLine("PASS Pinned Xray validates transports, full EasyGo and TUN DoH/StatsService configuration");
    passed++;

    await TestAsync("Classify a core startup failure separately from a proxy failure", async () => {
        var port = Xray.FreePort();
        var error = await Fails(() => Xray.StartAsync(executable, "{", port, CancellationToken.None));
        Assert(error.FailedCheck == Check.Xray);
    });
    await TestAsync("Pinned Xray reports a reset after TCP separately from failed TCP opening", () => CheckOuterReset(executable));

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
    var tunnelExecutable = Environment.GetEnvironmentVariable("POMOGATOR_TEST_TUN_XRAY");
    if (string.IsNullOrEmpty(tunnelExecutable)) throw new InvalidOperationException("Missing scoped TUN binary for Windows CI.");
    Test("Administrator guard rejects a Windows token with the Administrators SID disabled", NativeTunTests.CheckPrivilegeGuard);
    for (var cycle = 1; cycle <= 10; cycle++)
        await TestAsync($"Native Wintun cycle {cycle}/10: duplex TLS VLESS, UDP/TCP DNS and recovery", () => NativeTunTests.Run(tunnelExecutable));
    await TestAsync("Scoped TUN filter permits a physical TLS peer only to the tunnel transport", () => PhysicalPeerTests.Run(tunnelExecutable));
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

static async Task CheckOuterReset(string executable)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    using var endpoint = new TcpListener(IPAddress.Loopback, 0);
    endpoint.Start();
    var peerPort = ((IPEndPoint)endpoint.LocalEndpoint).Port;
    var clientPort = Xray.FreePort();
    var profile = Profiles.Parse($"vless://{Id}@127.0.0.1:{peerPort}?security=tls&sni=fixture.example");
    var diagnostic = new TunSocketDiagnostic(IPAddress.Loopback, peerPort, rawTransport: true);
    using var resetDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
    var config = JsonSerializer.Serialize(new {
        log = new { loglevel = "info", access = "none" },
        inbounds = new[] { new { listen = "127.0.0.1", port = clientPort, protocol = "http", tag = "fixture" } },
        outbounds = new[] { XrayConfig.Outbound(profile, "vpn") },
        routing = new { rules = new[] { new { type = "field", ruleTag = "pmt-control-vless",
            inboundTag = new[] { "fixture" }, outboundTag = "vpn" } } }
    });
    using var core = await Xray.StartAsync(executable, config, clientPort, deadline.Token, diagnosticLine: diagnostic.Read);
    var accepted = 0;
    async Task ResetConnections()
    {
        var hello = new byte[1];
        while (true)
        {
            using var socket = await endpoint.AcceptSocketAsync(resetDeadline.Token);
            Assert(await socket.ReceiveAsync(hello, SocketFlags.None, resetDeadline.Token) == 1 && hello[0] == 0x16);
            socket.LingerState = new LingerOption(true, 0);
            Interlocked.Increment(ref accepted);
        }
    }
    var reset = ResetConnections();
    using var http = Xray.ThroughProxy(clientPort);
    try
    {
        await Fails(() => Xray.VerifyAsync(http, new Uri("https://fixture.example/"), deadline.Token));
        var report = await ReadFailure("ввод-вывод после TCP");
        Assert(Volatile.Read(ref accepted) > 0);
        if (!report.Any(line => line.Contains("контроль через Xray → IP:порт VPN-сервера · ввод-вывод после TCP") &&
            (!OperatingSystem.IsWindows() || line.Contains("WinSock 10054"))))
            throw new InvalidOperationException("No classified reset after TCP. " + string.Join(" | ", report));
        Assert(!report.Any(line => line.Contains("открытие TCP-соединения") || line.Contains("внешний транспорт открыт")));
    }
    finally
    {
        resetDeadline.Cancel();
        try { await reset; }
        catch (OperationCanceledException) when (resetDeadline.IsCancellationRequested) { }
    }
    endpoint.Stop();
    await Fails(() => Xray.VerifyAsync(http, new Uri("https://fixture.example/"), deadline.Token));
    var refused = await ReadFailure(OperatingSystem.IsWindows() ? "подключение TCP (ConnectEx)" : "открытие TCP-соединения");
    if (!refused.Any(line => line.Contains("контроль через Xray → IP:порт VPN-сервера") &&
        (OperatingSystem.IsWindows() ? line.Contains("ConnectEx") && line.Contains("WinSock 10061") : line.Contains("открытие TCP"))))
        throw new InvalidOperationException("No classified TCP refusal. " + string.Join(" | ", refused));

    async Task<List<string>> ReadFailure(string expected)
    {
        var report = new List<string>();
        for (var attempt = 0; attempt < 15; attempt++)
        {
            report.AddRange(diagnostic.TakeEvents());
            if (report.Any(line => line.Contains(expected))) break;
            await Task.Delay(100, deadline.Token);
        }
        return report;
    }
}

static async Task CheckConfiguration(string executable, Profile profile, string? config = null)
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
        await process.StandardInput.WriteAsync(config ?? XrayConfig.Create(profile, 34000, 34001, 34002));
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
