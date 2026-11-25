using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Pomogator.Vpn;

// Only the test VLESS server maps Host:443 to this random loopback port.
// The production DNS client must negotiate TLS and POST a DNS message over HTTP/2.
internal sealed class DohFixture : IAsyncDisposable
{
    internal const string Host = "doh-fixture.test";
    private readonly WebApplication app;
    private readonly byte[] udpQuery = TunDns.Query("udp-" + Guid.NewGuid().ToString("N") + ".fixture.test");
    private readonly byte[] tcpQuery = TunDns.Query("tcp-" + Guid.NewGuid().ToString("N") + ".fixture.test");
    private int udpSeen, tcpSeen;

    internal DohFixture(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint =>
        {
            endpoint.Protocols = HttpProtocols.Http2;
            endpoint.UseHttps(certificate);
        }));
        app = builder.Build();
        app.MapPost("/dns-query", Answer);
    }

    internal int Port => new Uri(app.Urls.Single()).Port;
    internal Task StartAsync(CancellationToken token) => app.StartAsync(token);
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task Answer(HttpContext context)
    {
        var request = context.Request;
        if (request.Protocol != "HTTP/2" || request.Host.Host != Host ||
            request.ContentType != "application/dns-message" || request.ContentLength is not (> 12 and <= 4096))
        {
            context.Response.StatusCode = 400;
            return;
        }
        var packet = new byte[(int)request.ContentLength.Value];
        await request.Body.ReadExactlyAsync(packet, context.RequestAborted);
        // Xray chooses its own transaction ID and appends an EDNS padding record.
        // Match only the question and construct a response without the padded record.
        byte[] query;
        if (MatchesQuestion(packet, udpQuery))
        {
            Interlocked.Increment(ref udpSeen);
            query = udpQuery.ToArray();
        }
        else if (MatchesQuestion(packet, tcpQuery))
        {
            Interlocked.Increment(ref tcpSeen);
            query = tcpQuery.ToArray();
        }
        else
        {
            context.Response.StatusCode = 404;
            return;
        }
        Array.Copy(packet, query, 2);
        context.Response.ContentType = "application/dns-message";
        await context.Response.Body.WriteAsync(NativeTunTests.DnsAnswer(query), context.RequestAborted);
    }

    private static bool MatchesQuestion(byte[] packet, byte[] query) => packet.Length >= query.Length &&
        packet.AsSpan(12, query.Length - 12).SequenceEqual(query.AsSpan(12));

    internal async Task CheckThroughWintun(int index, IPAddress dns, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var destination = new IPEndPoint(dns, 53);
        using (var udp = TunWindows.OpenSocket(index, dns, SocketType.Dgram, ProtocolType.Udp))
        {
            await udp.ConnectAsync(destination, timeout.Token);
            await udp.SendAsync(udpQuery, SocketFlags.None, timeout.Token);
            var reply = new byte[4096];
            var size = await udp.ReceiveAsync(reply, SocketFlags.None, timeout.Token);
            VerifyAnswer(udpQuery, reply.AsSpan(0, size), Volatile.Read(ref udpSeen));
        }
        using var socket = TunWindows.OpenSocket(index, dns, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(destination, timeout.Token);
        using var stream = new NetworkStream(socket);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)tcpQuery.Length);
        await stream.WriteAsync(length, timeout.Token);
        await stream.WriteAsync(tcpQuery, timeout.Token);
        await stream.ReadExactlyAsync(length, timeout.Token);
        var tcpReply = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await stream.ReadExactlyAsync(tcpReply, timeout.Token);
        VerifyAnswer(tcpQuery, tcpReply, Volatile.Read(ref tcpSeen));
        Console.WriteLine("PASS Fresh UDP and TCP DNS questions through Wintun and VLESS reached the authenticated HTTP/2 DoH fixture");
    }

    private static void VerifyAnswer(byte[] query, ReadOnlySpan<byte> reply, int observed)
    {
        if (observed == 0 || TunDns.Read(query, reply, out _) != TunDnsAnswer.Resolved ||
            reply.IndexOf(new byte[] { 198, 51, 100, 10 }) < 0)
            throw new InvalidOperationException("Wintun did not return the IPv4 answer observed at the VLESS-only DoH responder.");
    }
}
