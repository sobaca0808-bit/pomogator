using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pomogator.Vpn;

internal static class LocalData
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "PomogatorVpn");
    internal static string Profiles => Path.Combine(Root, "profiles.dpapi");
    internal static string Recovery => Path.Combine(Root, "proxy-recovery.dpapi");
    internal static string TunRecovery => Path.Combine(Root, "tun-recovery.dpapi");

    internal static FileStream Lock()
    {
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var json = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(json); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    internal static void Save<T>(string path, T value)
    {
        var temp = path + ".new";
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(value);
            try
            {
                var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
                using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            finally { CryptographicOperations.ZeroMemory(json); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class SystemProxy
{
    private ProxyRecovery? active;
    internal bool Active => active != null;
    internal bool HasRecovery => File.Exists(LocalData.Recovery);

    internal void Enable(int port)
    {
        if (active != null || HasRecovery)
            throw new VpnError("Сначала обработайте сохранённую запись восстановления прокси.");
        var connection = RasProxyScope.Active();
        if (connection != null && new WinInet().Read().InUse)
            throw new VpnError("В Windows уже активен прокси LAN. Освободите его в запустившей программе.");
        var settings = new WinInet(connection);
        var recovery = ProxyControl.Prepare(settings, port) with { Connection = connection };
        LocalData.Save(LocalData.Recovery, recovery);
        active = recovery;
        ProxyControl.Apply(settings, recovery);
    }

    internal static bool AnyInUse()
    {
        if (new WinInet().Read().InUse) return true;
        var connection = RasProxyScope.Active();
        return connection != null && new WinInet(connection).Read().InUse;
    }

    internal bool ScopeChanged => active != null &&
        !string.Equals(active.Connection, RasProxyScope.Active(), StringComparison.OrdinalIgnoreCase);

    internal static HttpClient Client(int port)
    {
        // Let Windows/.NET resolve the effective proxy. Refuse to open a direct connection.
        var handler = new SocketsHttpHandler { ConnectCallback = async (context, token) =>
        {
            if (context.DnsEndPoint.Port != port || context.DnsEndPoint.Host != "127.0.0.1")
                throw new HttpRequestException("Windows did not select the verified loopback proxy.");
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        } };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    internal static async Task VerifyAsync(int port, CancellationToken token)
    {
        using var expected = Xray.ThroughProxy(port);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var system = Client(port);
            var results = await Task.WhenAll(InternetHealth.ExternalIpAsync(system, token), InternetHealth.ExternalIpAsync(expected, token));
            if (results[0] != null && results[0] == results[1]) return;
            if (attempt < 2) await Task.Delay(400, token);
        }
        throw new VpnError("Windows не подтвердила выход через системный прокси. Проверьте настройки прокси браузера или используйте локальный режим.", Check.Https);
    }

    internal bool ReleaseIfChanged()
    {
        if (active == null || new WinInet(active.Connection).Read() == active.Applied) return false;
        active = null;
        File.Delete(LocalData.Recovery);
        return true;
    }

    internal RestoreResult Restore(bool fromDisk = false)
    {
        var record = active ?? (fromDisk ? LocalData.Read<ProxyRecovery>(LocalData.Recovery) : null);
        if (record == null) return RestoreResult.AlreadyRestored;
        var result = ProxyControl.Restore(new WinInet(record.Connection), record);
        File.Delete(LocalData.Recovery);
        active = null;
        return result;
    }
}
