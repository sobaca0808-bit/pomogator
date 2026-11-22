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
    Console.WriteLine("PASS Pinned Xray validates all six generated configurations");
    passed++;

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
    var clientProfile = Profiles.Parse($"vless://{Id}@127.0.0.1:{port}");
    await Xray.CheckServerAsync(clientProfile, deadline.Token);
    var portLocal = Xray.FreePort();
    using var clientCore = await Xray.StartAsync(executable, XrayConfig.Create(clientProfile, portLocal), portLocal, deadline.Token, protect, owns);

    using var certKey = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", certKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var names = new SubjectAlternativeNameBuilder();
    names.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(names.Build());
    using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddMinutes(10));
    try
    {
        var endpoint = new Uri($"https://127.0.0.1:{destinationPort}/fixture");
        var responder = Respond(destination, certificate, deadline.Token);
        using var handler = new HttpClientHandler {
            Proxy = new WebProxy($"http://127.0.0.1:{portLocal}"),
            ServerCertificateCustomValidationCallback = (_, candidate, _, _) => candidate?.Thumbprint == certificate.Thumbprint
        };
        using var http = new HttpClient(handler);
        await Xray.VerifyAsync(http, endpoint, deadline.Token);
        await responder;
    }
    finally { destination.Stop(); }
    Console.WriteLine("PASS Verified HTTPS via HTTP proxy → Xray VLESS client → Xray VLESS server");
    passed++;
}
else Console.WriteLine("SKIP Core integration: set POMOGATOR_TEST_XRAY to the pinned binary.");

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
Console.WriteLine($"{passed} checks passed.");

void Test(string name, Action action)
{
    action();
    Console.WriteLine("PASS " + name);
    passed++;
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
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    foreach (var argument in new[] { "run", "-test", "-format", "json", "-config", "stdin:" }) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
    var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
    try
    {
        await process.StandardInput.WriteAsync(XrayConfig.Create(profile, 34000));
        process.StandardInput.Close();
        await process.WaitForExitAsync(deadline.Token);
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("Xray rejected " + profile.Security + "/" + profile.Transport + ".");
    }
    finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
}

static async Task Respond(TcpListener endpoint, X509Certificate2 cert, CancellationToken token)
{
    using var socket = await endpoint.AcceptTcpClientAsync(token);
    using var ssl = new SslStream(socket.GetStream());
    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, token);
    using var reader = new StreamReader(ssl, leaveOpen: true);
    Assert((await reader.ReadLineAsync(token))?.StartsWith("GET /fixture ") == true);
    while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
    await ssl.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
}

sealed class FakeProxy(ProxyState initial) : IProxySettings
{
    public ProxyState State = initial;
    public int Writes;
    public ProxyState Read() => State;
    public void Write(ProxyState state) { State = state; Writes++; }
}
