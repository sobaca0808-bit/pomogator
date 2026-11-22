using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

namespace Pomogator.Vpn;

internal static class TunControlProbe
{
    internal static HttpClient Client(TunControlEndpoint endpoint, int pid, string tlsHost,
        RemoteCertificateValidationCallback? validation = null)
    {
        var handler = new SocketsHttpHandler {
            Proxy = new WebProxy("http://127.0.0.1:" + endpoint.Port, BypassOnLocal: false) {
                Credentials = new NetworkCredential(endpoint.User, endpoint.Password)
            },
            UseProxy = true, AllowAutoRedirect = false, UseCookies = false
        };
        if (validation != null) handler.SslOptions.RemoteCertificateValidationCallback = validation;
        handler.ConnectCallback = async (context, token) =>
        {
            if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != endpoint.Port ||
                !WindowsLifetime.OwnsLoopback(endpoint.Port, pid))
                throw new IOException("The TUN engine no longer owns the control listener.");
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port, token);
                if (!WindowsLifetime.OwnsLoopback(endpoint.Port, pid)) throw new IOException("The control listener changed.");
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        return new HttpClient(new PinnedDestination(endpoint, tlsHost, handler)) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private sealed class PinnedDestination(TunControlEndpoint endpoint, string tlsHost, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri;
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || uri.Port != endpoint.DestinationPort ||
                !string.Equals(uri.IdnHost, tlsHost, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Unexpected control destination.");
            // The CONNECT authority uses the URI's pinned IP, while .NET uses Host for TLS SNI and name validation.
            request.Headers.Host = uri.IsDefaultPort ? tlsHost : tlsHost + ":" + uri.Port;
            request.RequestUri = new UriBuilder(uri) { Host = endpoint.Destination }.Uri;
            return base.SendAsync(request, token);
        }
    }
}
