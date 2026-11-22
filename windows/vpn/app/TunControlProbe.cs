using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Pomogator.Vpn;

internal static class TunControlProbe
{
    internal static HttpClient Client(TunControlEndpoint endpoint, int pid, string tlsHost,
        RemoteCertificateValidationCallback? validation = null, Action<string>? observe = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        if (validation != null) handler.SslOptions.RemoteCertificateValidationCallback = validation;
        handler.ConnectCallback = async (context, token) =>
        {
            if (!string.Equals(context.DnsEndPoint.Host, tlsHost, StringComparison.OrdinalIgnoreCase) ||
                context.DnsEndPoint.Port != endpoint.DestinationPort)
                throw new HttpRequestException("Unexpected control destination.");
            if (!WindowsLifetime.OwnsLoopback(endpoint.Port, pid))
                throw new IOException("The TUN engine no longer owns the control listener.");

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port, token);
                if (!WindowsLifetime.OwnsLoopback(endpoint.Port, pid)) throw new IOException("The control listener changed.");
                var stream = new NetworkStream(socket, ownsSocket: true);
                observe?.Invoke("loopback connected");
                await Connect(stream, endpoint, token);
                observe?.Invoke("authenticated CONNECT established");
                // The local CONNECT uses a literal IP. The HTTPS URI still controls SNI and name validation.
                return stream;
            }
            catch { socket.Dispose(); throw; }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private static async Task Connect(Stream stream, TunControlEndpoint endpoint, CancellationToken token)
    {
        var authority = endpoint.Destination + ":" + endpoint.DestinationPort;
        var secret = endpoint.User.Length == 0 ? [] : Encoding.UTF8.GetBytes(endpoint.User + ":" + endpoint.Password);
        var authentication = secret.Length == 0 ? "" : $"Proxy-Authorization: Basic {Convert.ToBase64String(secret)}\r\n";
        var header = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{authentication}\r\n");
        try { await stream.WriteAsync(header, token); }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(header);
        }

        var response = new byte[4096];
        // Do not buffer past the end of the CONNECT response. HttpClient takes over the same stream for TLS.
        for (var count = 0; count < response.Length; count++)
        {
            await stream.ReadExactlyAsync(response.AsMemory(count, 1), token);
            if (count < 3 || response[count - 3] != '\r' || response[count - 2] != '\n' ||
                response[count - 1] != '\r' || response[count] != '\n') continue;

            var firstLine = Array.IndexOf(response, (byte)'\r', 0, count + 1);
            var fields = Encoding.ASCII.GetString(response, 0, firstLine).Split(' ', 3);
            if (fields.Length >= 2 && fields[0] is "HTTP/1.1" or "HTTP/1.0" && int.TryParse(fields[1], out var status))
            {
                if (status == 200) return;
                throw new HttpRequestException("Control CONNECT was rejected.", null, (HttpStatusCode)status);
            }
            break;
        }
        throw new IOException("Invalid CONNECT response.");
    }
}
