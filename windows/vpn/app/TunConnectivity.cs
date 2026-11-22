using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Pomogator.Vpn;

internal sealed record TunProbe(bool Success, string Outcome, long Milliseconds)
{
    internal string Line(string label) => $"{label}: {Outcome} ({Milliseconds} мс)";
}

internal sealed record TunChecks(TunProbe Resolver, TunProbe Windows, TunProbe Https, string[]? Traffic = null,
    TunProbe? DirectPinned = null, TunProbe? SameEngine = null)
{
    internal bool Success => Resolver.Success && Windows.Success && Https.Success;
    private bool BothHttpsFailed => DirectPinned is { Success: false } && SameEngine is { Success: false } && !Https.Success;
    internal string FailedStage => BothHttpsFailed ? "исходящий HTTPS через VLESS при активном TUN" :
        !Resolver.Success ? "запрос к DNS на Wintun" :
        !Windows.Success ? "системное разрешение DNS" : "HTTPS через Wintun";
    internal string Failure => BothHttpsFailed ?
        "Обе HTTPS-пробы по IPv4 завершились с ошибкой. Проверьте исходящий маршрут и соединение Xray с VPN-сервером." :
        !Resolver.Success && !Windows.Success ?
        "DNS-сервер TUN и системный DNS Windows не подтвердили разрешение имени." :
        !Resolver.Success ? "DNS-сервер TUN не подтвердил ответ через виртуальный интерфейс." :
        !Windows.Success ? "DNS-сервер ответил через Wintun, но системный DNS Windows не разрешил контрольное имя." :
        "DNS подтверждён, но контрольный HTTPS через Wintun не прошёл.";

    internal IEnumerable<string> Lines()
    {
        yield return Resolver.Line("DNS-запрос к " + TunConfig.Resolver + " через Wintun");
        yield return Windows.Line("Системное разрешение имён Windows");
        yield return Https.Line("HTTPS через Wintun без системного прокси");
        if (DirectPinned != null) yield return DirectPinned.Line("HTTPS напрямую через Wintun, закреплённая цель");
        if (SameEngine != null) yield return SameEngine.Line("HTTPS через локальный вход того же Xray при активном TUN");
        if (DirectPinned != null && SameEngine != null) yield return Compare(DirectPinned.Success, SameEngine.Success);
        foreach (var line in Traffic ?? Array.Empty<string>()) yield return line;
    }
    private static string Compare(bool direct, bool control) => (direct, control) switch
    {
        (true, true) => "Сравнение HTTPS · обе проверки прошли на одной IPv4-цели.",
        (false, true) => "Сравнение HTTPS · VLESS в том же Xray доступен. Сбой наблюдается на пути через Wintun.",
        (true, false) => "Сравнение HTTPS · Wintun доступен. Не прошёл контроль через локальный вход Xray.",
        _ => "Сравнение HTTPS · обе проверки не подтверждены. Проверьте коды и события исходящего соединения Xray."
    };
}

internal static class TunConnectivity
{
    internal const string ControlName = "www.cloudflare.com";
    private const string AlternateName = "example.com";

    internal static Task<TunProbe> CheckBeforeTun(int port, int pid, CancellationToken token) =>
        PinnedHttps(validation => TunControlProbe.Client(new TunControlEndpoint(port, "", "", TunConfig.InternetProbe, 443),
            pid, TunConfig.InternetProbeHost, validation), token);

    internal static async Task<TunChecks> Run(TunSession session, CancellationToken token)
    {
        var record = session.Record;
        var adapter = TunWindows.Find(record.Id) ?? throw new VpnError("Интерфейс Wintun исчез перед проверкой трафика.");
        var index = TunWindows.Index(adapter);
        var resolver = IPAddress.Parse(record.InstalledDns);
        var lines = new List<string>();
        _ = await session.SampleEvents(token); // Discard events before this diagnostic interval.
        Task<TunTrafficSample> Capture() => TunTrafficSample.Capture(record.Id, session.DiagnosticsPort, session.EnginePid, token);

        var previous = await Capture();
        // Compare the two DNS-independent probes before launching our own DNS retries.
        TunProbe? control = null;
        if (session.Control is { } endpoint)
        {
            var probe = PinnedHttps(validation => TunControlProbe.Client(endpoint, session.EnginePid,
                TunConfig.InternetProbeHost, validation), token);
            var trace = await ObservePeer(probe, session, token);
            control = await probe;
            lines.Add(trace);
            var afterControl = await Capture();
            lines.AddRange(afterControl.Describe("HTTPS локальный вход Xray", previous, explainFailure: false));
            previous = afterControl;
            await AddEngineEvents("HTTPS локальный вход Xray");
        }
        else lines.Add("HTTPS локальный вход Xray · помощник не подтвердил контрольный вход.");

        var pinned = await PinnedHttps(validation => TunWindows.Http(record.Id, validation,
            TunConfig.InternetProbeHost, IPAddress.Parse(TunConfig.InternetProbe)), token);
        var afterHttps = await Capture();
        lines.AddRange(afterHttps.Describe("HTTPS Wintun", previous, !pinned.Success));
        await AddEngineEvents("HTTPS Wintun");

        var direct = CheckResolver(resolver, index, token);
        var windows = CheckWindows(token);
        await Task.WhenAll(direct, windows);
        var directResult = await direct;
        var windowsResult = await windows;
        var afterDns = await Capture();
        lines.AddRange(afterDns.Describe("DNS", afterHttps, !directResult.Success || !windowsResult.Success));
        await AddEngineEvents("DNS");

        var https = pinned;
        if (!pinned.Success && TunWindows.Find(record.Id) != null)
        {
            try
            {
                using var fallback = TunWindows.Http(record.Id);
                var alternative = await InternetHealth.ProbeAsync(fallback, token);
                if (alternative is { } elapsed)
                    https = new(true, "альтернативный контрольный адрес доступен через Wintun", (long)elapsed.TotalMilliseconds);
            }
            catch (VpnError) { lines.Add("Резервная HTTPS-проба · интерфейс Wintun больше недоступен."); }
        }
        return new TunChecks(directResult, windowsResult, https, lines.ToArray(), pinned, control);

        async Task AddEngineEvents(string phase)
        {
            var events = await session.SampleEvents(token);
            if (events is { Length: > 0 })
                lines.AddRange(events.Select(item => $"{phase} · Xray · {item}"));
            else lines.Add($"{phase} · Xray · {(events == null ? "события недоступны" : "распознанных событий нет")}.");
        }
    }

    private static async Task<string> ObservePeer(Task probe, TunSession session, CancellationToken token)
    {
        var peer = IPAddress.Parse(session.Record.Peer);
        var source = IPAddress.Parse(session.Record.Before.SourceIpv4!);
        PeerTcp? observed = null;
        do
        {
            if (WindowsLifetime.ReadPeerTcp(session.EnginePid, peer, session.PeerPort, source) is { } sample)
                observed = (observed ?? PeerTcp.None) | sample;
            if (probe.IsCompleted) break;
            token.ThrowIfCancellationRequested();
            await Task.WhenAny(probe, Task.Delay(40, token));
        } while (true);
        return PeerLine(observed, TunWindows.Index(peer), session.Record.Before.Uplink);
    }

    internal static string PeerLine(PeerTcp? observed, int route, int expected)
    {
        var prefix = $"Снимок TCP процесса Xray к VPN-серверу · маршрут IF {route} (исходный IF {expected})";
        if (observed == null) return prefix + "; таблица TCP недоступна.";
        var flags = observed.Value;
        if (!flags.HasFlag(PeerTcp.Seen)) return prefix + "; сокет не зафиксирован за время наблюдения.";
        var details = new List<string>();
        if (flags.HasFlag(PeerTcp.ExpectedSource)) details.Add("ожидаемый IPv4 источника");
        if (flags.HasFlag(PeerTcp.TunSource)) details.Add("IPv4 источника принадлежит Wintun");
        if (flags.HasFlag(PeerTcp.OtherSource)) details.Add("другой IPv4 источника");
        if (flags.HasFlag(PeerTcp.SynSent)) details.Add("SYN_SENT");
        if (flags.HasFlag(PeerTcp.Established)) details.Add("ESTABLISHED");
        if (details.Count == 0) details.Add("сокет найден, источник или стадия не подтверждены");
        return prefix + "; " + string.Join(", ", details) + ".";
    }

    internal static async Task<TunProbe> CheckResolver(IPAddress resolver, int index, CancellationToken token, int port = 53)
    {
        var watch = Stopwatch.StartNew();
        string outcome = "DNS не ответил";
        var destination = new IPEndPoint(resolver, port);
        foreach (var name in new[] { ControlName, AlternateName })
        {
            var query = TunDns.Query(name);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var udp = TunWindows.OpenSocket(index, resolver, SocketType.Dgram, ProtocolType.Udp);
                await udp.ConnectAsync(destination, attempt.Token);
                await udp.SendAsync(query, SocketFlags.None, attempt.Token);
                var reply = new byte[4096];
                var length = await udp.ReceiveAsync(reply, SocketFlags.None, attempt.Token);
                var answer = TunDns.Read(query, reply.AsSpan(0, length), out var code);
                if (answer == TunDnsAnswer.Truncated)
                {
                    (answer, code) = await ReadTcp(index, destination, query, token);
                    if (answer == TunDnsAnswer.Resolved)
                        return new(true, "получен IPv4-ответ по TCP после усечённого UDP", watch.ElapsedMilliseconds);
                    outcome = "TCP после усечённого UDP: " + Describe(answer, code);
                }
                else
                {
                    if (answer == TunDnsAnswer.Resolved)
                        return new(true, "получен IPv4-ответ по UDP", watch.ElapsedMilliseconds);
                    outcome = "UDP: " + Describe(answer, code);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                outcome = "UDP: истекло время ответа DNS (DNS_TIMEOUT)";
                try
                {
                    var (tcpAnswer, tcpCode) = await ReadTcp(index, destination, query, token);
                    if (tcpAnswer == TunDnsAnswer.Resolved)
                        return new(true, "получен IPv4-ответ по TCP после тайм-аута UDP", watch.ElapsedMilliseconds);
                    outcome += "; TCP: " + Describe(tcpAnswer, tcpCode);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { outcome += "; TCP: DNS_TIMEOUT"; }
                catch (SocketException error) { outcome += "; TCP: код Windows " + error.NativeErrorCode; }
                catch (IOException) { outcome += "; TCP: DNS_INCOMPLETE"; }
            }
            catch (SocketException error) { outcome = "ошибка сокета Windows " + error.NativeErrorCode; }
            catch (IOException) { outcome = "неполный ответ DNS по TCP (DNS_INCOMPLETE)"; }
        }
        return new(false, outcome, watch.ElapsedMilliseconds);
    }

    private static async Task<(TunDnsAnswer Answer, int Code)> ReadTcp(int index, IPEndPoint destination, byte[] query, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(TimeSpan.FromSeconds(5));
        using var tcp = TunWindows.OpenSocket(index, destination.Address, SocketType.Stream, ProtocolType.Tcp);
        await tcp.ConnectAsync(destination, attempt.Token);
        await using var stream = new NetworkStream(tcp);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)query.Length);
        await stream.WriteAsync(length, attempt.Token);
        await stream.WriteAsync(query, attempt.Token);
        await stream.ReadExactlyAsync(length, attempt.Token);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await stream.ReadExactlyAsync(response, attempt.Token);
        var answer = TunDns.Read(query, response, out var code);
        return (answer, code);
    }

    private static string Describe(TunDnsAnswer answer, int code) => answer switch
    {
        TunDnsAnswer.ServerError => "сервер вернул RCODE=" + code,
        TunDnsAnswer.NoAddress => "в ответе нет записи A (RCODE=0)",
        TunDnsAnswer.Truncated => "ответ усечён",
        _ => "ответ не соответствует запросу (DNS_INVALID_RESPONSE)"
    };

    private static async Task<TunProbe> CheckWindows(CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        string outcome = "ответ Windows отсутствует";
        foreach (var name in new[] { ControlName, AlternateName })
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if ((await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, attempt.Token)).Length != 0)
                    return new(true, "Windows вернула IPv4-адрес", watch.ElapsedMilliseconds);
                outcome = "Windows не вернула IPv4-адрес";
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { outcome = "истекло время системного запроса (DNS_TIMEOUT)"; }
            catch (SocketException error) { outcome = "ошибка системного DNS Windows " + error.NativeErrorCode; }
        }
        return new(false, outcome, watch.ElapsedMilliseconds);
    }

    private static async Task<TunProbe> PinnedHttps(Func<RemoteCertificateValidationCallback, HttpClient> open,
        CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        SslPolicyErrors certificate = SslPolicyErrors.None;
        string? chainStatus = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        string outcome;
        try
        {
            using var client = open((_, _, chain, errors) =>
            {
                certificate = errors;
                if (errors != SslPolicyErrors.None && chain != null)
                    chainStatus = string.Join(", ", chain.ChainStatus.Select(status => status.Status));
                return errors == SslPolicyErrors.None;
            });
            // Known IP keeps this probe independent of DNS. The URI supplies the real SNI and certificate name.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://" + TunConfig.InternetProbeHost + "/dns-query?name=example.com&type=A");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                if (await body.ReadAsync(new byte[1], deadline.Token) == 1)
                    return new(true, "TLS с проверенным именем, HTTP 200 и данные ответа", watch.ElapsedMilliseconds);
                outcome = "HTTP 200 без данных ответа";
            }
            else outcome = "контрольный IPv4 вернул HTTP " + (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { outcome = "истекло время HTTPS по контрольному IPv4"; }
        catch (VpnError) { outcome = "Windows не подтвердила интерфейс для HTTPS-пробы"; }
        catch (IOException error) { outcome = "сбой чтения ответа HTTPS" + ErrorDetails(error); }
        catch (SocketException error) { outcome = "сбой сокета контрольной HTTPS-пробы" + ErrorDetails(error); }
        catch (HttpRequestException error)
        {
            outcome = error.StatusCode == HttpStatusCode.ProxyAuthenticationRequired ?
                "локальный вход не подтвердил учётные данные контрольной пробы" :
                "HTTPS по контрольному IPv4: " + error.HttpRequestError;
            if (certificate != SslPolicyErrors.None) outcome += "; сертификат: " + certificate;
            if (!string.IsNullOrEmpty(chainStatus)) outcome += "; цепочка: " + chainStatus;
            outcome += ErrorDetails(error.InnerException);
        }

        return new(false, outcome, watch.ElapsedMilliseconds);
    }

    private static string ErrorDetails(Exception? cause)
    {
        var details = new StringBuilder();
        for (; cause != null; cause = cause.InnerException)
            if (cause is SocketException socket)
                details.Append(" · код сокета этой HTTPS-пробы ").Append(socket.NativeErrorCode);
            else if (cause is AuthenticationException or IOException)
                details.Append($" · {cause.GetType().Name} 0x{cause.HResult:X8}");
        return details.ToString();
    }
}

internal enum TunDnsAnswer { Invalid, Resolved, Truncated, ServerError, NoAddress }

internal static class TunDns
{
    internal static byte[] Query(string name)
    {
        using var packet = new MemoryStream();
        packet.Write(RandomNumberGenerator.GetBytes(2));
        packet.Write(new byte[] { 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // Recursion desired, one question.
        foreach (var label in name.Split('.'))
        {
            packet.WriteByte((byte)label.Length);
            packet.Write(Encoding.ASCII.GetBytes(label));
        }
        packet.Write(new byte[] { 0, 0, 1, 0, 1 }); // A, IN.
        return packet.ToArray();
    }

    internal static TunDnsAnswer Read(ReadOnlySpan<byte> query, ReadOnlySpan<byte> packet, out int code)
    {
        code = -1;
        if (packet.Length < 12 || !packet[..2].SequenceEqual(query[..2]) ||
            (packet[2] & 0xf8) != 0x80 || BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != 1)
            return TunDnsAnswer.Invalid;
        var question = 12;
        if (!SkipName(packet, ref question) || packet.Length - question < 4 ||
            !packet[12..(question + 4)].SequenceEqual(query[12..])) return TunDnsAnswer.Invalid;
        if ((packet[2] & 0x02) != 0) return TunDnsAnswer.Truncated;
        code = packet[3] & 0x0f;
        if (code != 0) return TunDnsAnswer.ServerError;
        var offset = question + 4;
        var answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        for (int i = 0; i < answers; i++)
        {
            if (!SkipName(packet, ref offset) || packet.Length - offset < 10) return TunDnsAnswer.Invalid;
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var family = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            if (packet.Length - offset < length) return TunDnsAnswer.Invalid;
            if (type == 1 && family == 1 && length == 4) return TunDnsAnswer.Resolved;
            offset += length;
        }
        return TunDnsAnswer.NoAddress;
    }

    private static bool SkipName(ReadOnlySpan<byte> packet, ref int offset)
    {
        while (offset < packet.Length)
        {
            var count = packet[offset++];
            if (count == 0) return true;
            if ((count & 0xc0) == 0xc0 && offset < packet.Length)
                return ((count & 0x3f) * 256 + packet[offset++]) < offset - 2;
            if (count > 63 || packet.Length - offset < count) return false;
            offset += count;
        }
        return false;
    }
}
