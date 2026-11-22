using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Pomogator.Vpn;

internal sealed record TunProbe(bool Success, string Outcome, long Milliseconds)
{
    internal string Line(string label) => $"{label}: {Outcome} ({Milliseconds} мс)";
}

internal sealed record TunChecks(TunProbe Resolver, TunProbe Windows, TunProbe Https)
{
    internal bool Success => Resolver.Success && Windows.Success && Https.Success;
    internal string FailedStage => !Resolver.Success ? "запрос к DNS на Wintun" :
        !Windows.Success ? "системное разрешение DNS" : "HTTPS через Wintun";
    internal string Failure => !Resolver.Success && !Windows.Success ?
        "DNS-сервер TUN и системный DNS Windows не подтвердили разрешение имени." :
        !Resolver.Success ? "DNS-сервер TUN не подтвердил ответ через виртуальный интерфейс." :
        !Windows.Success ? "DNS-сервер ответил через Wintun, но системный DNS Windows не разрешил контрольное имя." :
        "DNS подтверждён, но контрольный HTTPS через Wintun не прошёл.";

    internal IEnumerable<string> Lines()
    {
        yield return Resolver.Line("DNS-запрос к " + TunConfig.Resolver + " через Wintun");
        yield return Windows.Line("Системное разрешение имён Windows");
        yield return Https.Line("HTTPS через Wintun без системного прокси");
    }
}

internal static class TunConnectivity
{
    internal const string ControlName = "www.cloudflare.com";
    private const string AlternateName = "example.com";

    internal static async Task<TunChecks> Run(TunRecovery record, CancellationToken token)
    {
        var adapter = TunWindows.Find(record.Id) ?? throw new VpnError("Интерфейс Wintun исчез перед проверкой трафика.");
        var index = TunWindows.Index(adapter);
        var resolver = IPAddress.Parse(record.InstalledDns);
        var direct = CheckResolver(resolver, index, token);
        var windows = CheckWindows(token);
        var https = CheckHttps(record.Id, token);
        await Task.WhenAll(direct, windows, https);
        return new TunChecks(await direct, await windows, await https);
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
            attempt.CancelAfter(TimeSpan.FromSeconds(2.5));
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
        attempt.CancelAfter(TimeSpan.FromSeconds(2));
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
            attempt.CancelAfter(TimeSpan.FromSeconds(3));
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

    private static async Task<TunProbe> CheckHttps(Guid id, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var client = TunWindows.Http(id);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        string outcome;
        try
        {
            // Use an IP endpoint with a matching TLS certificate to test HTTPS independently from DNS.
            using var response = await client.GetAsync("https://" + TunConfig.InternetProbe + "/cdn-cgi/trace",
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
                return new(true, "TLS и HTTP 200 по контрольному IPv4", watch.ElapsedMilliseconds);
            outcome = "контрольный IPv4 вернул HTTP " + (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { outcome = "истекло время HTTPS по контрольному IPv4"; }
        catch (HttpRequestException error) { outcome = "HTTPS по контрольному IPv4: " + error.HttpRequestError; }

        // One provider can be unreachable even when the tunnel is healthy. Named checks still use the bound Wintun socket.
        var fallback = await InternetHealth.ProbeAsync(client, token);
        return fallback.HasValue ? new(true, "HTTPS по контрольному имени; " + outcome, watch.ElapsedMilliseconds) :
            new(false, outcome + "; HTTPS по контрольным именам недоступен", watch.ElapsedMilliseconds);
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
