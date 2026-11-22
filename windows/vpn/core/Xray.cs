using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Pomogator.Vpn;

public sealed class Xray : IDisposable
{
    private readonly Process process;
    private IDisposable? lifetime;
    private bool disposed;
    public int Port { get; }
    public bool Running => !disposed && !process.HasExited;

    private Xray(Process process, int port) { this.process = process; Port = port; }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public static async Task<Xray> StartAsync(string executable, string config, int port, CancellationToken token,
        Func<Process, IDisposable>? protect = null, Func<int, int, bool>? ownsPort = null)
    {
        var info = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var arg in new[] { "run", "-format", "json", "-config", "stdin:" }) info.ArgumentList.Add(arg);
        info.Environment.Remove("XRAY_LOCATION_CONFIG");
        info.Environment.Remove("XRAY_LOCATION_CONFDIR");
        info.Environment.Remove("XRAY_VMESS_AEAD_FORCED");
        var process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new InvalidOperationException(); }
        catch
        {
            process.Dispose();
            throw new VpnError("Не удалось запустить ядро. Проверьте комплект приложения и разрешение Windows на запуск.");
        }

        var result = new Xray(process, port);
        try
        {
            result.lifetime = protect?.Invoke(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(6));
            await process.StandardInput.WriteAsync(config.AsMemory(), startup.Token);
            await process.StandardInput.FlushAsync(startup.Token);
            process.StandardInput.Close();

            while (true)
            {
                if (!result.Running)
                    throw new VpnError("Ядро отклонило профиль или завершило запуск. Проверьте параметры TLS, REALITY и транспорта.");
                if ((ownsPort == null || ownsPort(port, process.Id)) && await Listening(port, startup.Token)) return result;
                await Task.Delay(100, startup.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            result.Dispose();
            throw new VpnError("За 6 секунд ядро не открыло локальный прокси.");
        }
        catch (IOException)
        {
            result.Dispose();
            throw new VpnError("Ядро завершилось до загрузки профиля.");
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public static async Task CheckServerAsync(Profile profile, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var client = new TcpClient();
        try { await client.ConnectAsync(profile.Host, profile.Port, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new VpnError("VPN-сервер не ответил на TCP-проверку за 6 секунд."); }
        catch (SocketException error)
        {
            throw new VpnError(error.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                ? "Windows не разрешила имя VPN-сервера. Проверьте адрес и DNS."
                : "VPN-сервер недоступен по TCP. Проверьте адрес, порт и своё подключение.");
        }
    }

    public static HttpClient ThroughProxy(int port)
    {
        var handler = new HttpClientHandler {
            UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{port}"), AllowAutoRedirect = false,
            UseCookies = false, UseDefaultCredentials = false
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public static async Task VerifyAsync(int port, CancellationToken token)
    {
        using var client = ThroughProxy(port);
        await VerifyAsync(client, new Uri("https://www.gstatic.com/generate_204"), token);
    }

    public static async Task VerifyAsync(HttpClient client, Uri endpoint, CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new VpnError("Контрольный HTTPS-запрос не вернул ожидаемый HTTP 204. Прохождение трафика через VPN не подтверждено.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new VpnError("Контрольный HTTPS-запрос через прокси не ответил за 12 секунд. Проверьте профиль и доступ к контрольному адресу."); }
        catch (HttpRequestException)
        { throw new VpnError("HTTPS через прокси не прошёл. Возможны ошибка TLS, недоступность сайта или проблема с DNS на стороне VPN."); }
    }

    private static async Task<bool> Listening(int port, CancellationToken token)
    {
        using var socket = new TcpClient();
        try { await socket.ConnectAsync(IPAddress.Loopback, port, token); return true; }
        catch (SocketException) { return false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime?.Dispose();
        lifetime = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}
