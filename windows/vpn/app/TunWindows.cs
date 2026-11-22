using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Pomogator.Vpn;

internal sealed record TunBaseline(int Uplink, string UplinkId, string[] Addresses, string[] Dns, int InternetRoute);
internal sealed record TunRecovery(Guid Id, string Peer, TunBaseline Before, string[]? InstalledRoutes = null,
    string InstalledDns = TunConfig.Resolver);
internal sealed record TunObservation(bool Interface, bool Routes, bool Dns);

internal static class TunWindows
{
    internal static bool Administrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool SupportsWindows => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045) &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    internal static bool Conflicts(IEnumerable<(string Name, bool Up, NetworkInterfaceType Type)> adapters) =>
        adapters.Any(adapter => adapter.Up &&
            (adapter.Type == NetworkInterfaceType.Tunnel || new[] { "uboost", "wintun", "wireguard",
                "tap-windows", "openvpn", "tailscale", "zerotier", "vpn" }.Any(word =>
                    adapter.Name.Contains(word, StringComparison.OrdinalIgnoreCase))));

    internal static bool OtherVpn(string? own = null)
    {
        if (Conflicts(NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.Name != own)
            .Select(adapter => (adapter.Name + " " + adapter.Description,
                adapter.OperationalStatus == OperationalStatus.Up, adapter.NetworkInterfaceType)))) return true;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.ProcessName.Contains("uboost", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }
        return false;
    }

    internal static TunBaseline Capture(IPAddress peer)
    {
        var upstream = Index(peer);
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            item.OperationalStatus == OperationalStatus.Up && Index(item) == upstream);
        if (adapter == null || upstream == 0) throw new VpnError("Не найден рабочий маршрут IPv4 к VPN-серверу.");
        var props = adapter.GetIPProperties();
        return new TunBaseline(upstream, adapter.Id, props.UnicastAddresses.Select(item => item.Address.ToString())
            .Order().ToArray(), props.DnsAddresses.Select(ip => ip.ToString()).Order().ToArray(),
            Index(IPAddress.Parse(TunConfig.Resolver)));
    }

    internal static bool Same(TunBaseline expected, TunBaseline actual) =>
        expected.Uplink == actual.Uplink && expected.UplinkId == actual.UplinkId &&
        expected.Addresses.SequenceEqual(actual.Addresses) && expected.Dns.SequenceEqual(actual.Dns);

    internal static NetworkInterface? Find(Guid id) => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
        adapter => adapter.Name == TunConfig.Adapter(id) && Guid.TryParse(adapter.Id, out var guid) && guid == TunConfig.AdapterId(id));

    internal static TunObservation Observe(TunRecovery record)
    {
        var adapter = Find(record.Id);
        if (adapter == null || adapter.OperationalStatus != OperationalStatus.Up) return new(false, false, false);
        var properties = adapter.GetIPProperties();
        var index = Index(adapter);
        var peer = IPAddress.Parse(record.Peer);
        var routes = index != 0 && Index(peer) == record.Before.Uplink && Index(IPAddress.Parse(TunConfig.Resolver)) == index &&
            Index(IPAddress.Parse("1.1.1.1")) == index;
        return new TunObservation(properties.UnicastAddresses.Any(address => address.Address.ToString() == TunConfig.Address),
            routes, properties.DnsAddresses.Any(address => address.ToString() == TunConfig.Resolver));
    }

    internal static HttpClient Http(Guid id, RemoteCertificateValidationCallback? validation = null)
    {
        var adapter = Find(id) ?? throw new VpnError("Интерфейс TUN пропал.");
        var adapterIndex = Index(adapter);
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        if (validation != null) handler.SslOptions.RemoteCertificateValidationCallback = validation;
        handler.ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
            foreach (var ip in addresses)
            {
                if (Index(ip) != adapterIndex) continue;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // Bind to the TUN source address. Windows still performs TLS validation for the original host.
                    socket.Bind(new IPEndPoint(IPAddress.Parse(TunConfig.Address), 0));
                    await socket.ConnectAsync(ip, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("No usable IPv4 route through TUN.");
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    internal static bool Vacant(TunRecovery record)
    {
        var prefix = IPNetwork.Parse("198.18.240.0/30");
        return Clean(record) && NetworkInterface.GetAllNetworkInterfaces().All(adapter =>
            adapter.Name != TunConfig.Adapter(record.Id) &&
            !adapter.GetIPProperties().UnicastAddresses.Any(entry => entry.Address.AddressFamily == AddressFamily.InterNetwork
                && prefix.Contains(entry.Address)));
    }

    internal static async Task<bool> CheckDns(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try { return (await Dns.GetHostAddressesAsync("www.cloudflare.com", AddressFamily.InterNetwork, limit.Token)).Length > 0; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    internal static bool Clean(TunRecovery record)
    {
        if (Find(record.Id) != null) return false;
        using var registry = Registry.LocalMachine.OpenSubKey(ConnectionKey(record.Id));
        return registry == null;
    }

    internal static async Task<bool> Recover(TunRecovery record, Action<string>? diagnostic = null)
    {
        if (Clean(record)) return true;
        var adapter = Find(record.Id);
        // An unrecognized device or foreign additions are left for manual inspection.
        if (adapter == null) return Refuse("adapter-not-visible");
        var props = adapter.GetIPProperties();
        if (props.UnicastAddresses.Any(a => !a.Address.IsIPv6LinkLocal && a.Address.ToString() != TunConfig.Address &&
                !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) ||
            props.DnsAddresses.Any(a => a.ToString() != record.InstalledDns)) return Refuse("unrecognized-address-or-dns");
        var routes = await ReadRoutes(Index(adapter));
        if (routes == null) return Refuse("could-not-read-routes");
        var allowed = (record.InstalledRoutes ?? TunConfig.Routes(IPAddress.Parse(record.Peer))).Concat(new[] {
            TunConfig.Address + "/32", "198.18.240.0/30", "198.18.240.3/32", "255.255.255.255/32",
            "224.0.0.0/4", "ff00::/8", "fe80::/64", "169.254.0.0/16"
        }).ToHashSet();
        if (routes.Any(route => !allowed.Contains(route) && !(route.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase) &&
            route.EndsWith("/128", StringComparison.Ordinal)))) return Refuse("unrecognized-route");
        using var registry = Registry.LocalMachine.OpenSubKey(ConnectionKey(record.Id));
        var pnp = registry?.GetValue("PnpInstanceID") as string;
        if (pnp == null || !(pnp.StartsWith("SWD\\WINTUN\\", StringComparison.OrdinalIgnoreCase) ||
            pnp.StartsWith("ROOT\\WINTUN\\", StringComparison.OrdinalIgnoreCase))) return Refuse("unrecognized-device-instance");

        var result = await Run(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), "/remove-device", pnp);
        if (result?.Exit != 0) return Refuse("device-remove-failed");
        return await WaitClean(record, TimeSpan.FromSeconds(5)) || Refuse("device-still-present");

        bool Refuse(string reason)
        {
            diagnostic?.Invoke(reason);
            return false;
        }
    }

    internal static async Task<bool> WaitClean(TunRecovery record, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (!Clean(record) && watch.Elapsed < limit) await Task.Delay(200);
        return Clean(record);
    }

    private static string ConnectionKey(Guid id) =>
        @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\" + TunConfig.AdapterId(id).ToString("B") + @"\Connection";

    private static async Task<string[]?> ReadRoutes(int index)
    {
        if (index <= 0) return null;
        var command = "$ErrorActionPreference='Stop'; Get-NetRoute -InterfaceIndex " + index + " | Select-Object -ExpandProperty DestinationPrefix";
        var payload = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var result = await Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile", "-NonInteractive", "-EncodedCommand", payload);
        return result?.Exit == 0 ? result.Value.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
    }

    private static async Task<(int Exit, string Output)?> Run(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process == null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            return (process.ExitCode, await read);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return null;
        }
    }

    internal static int Index(NetworkInterface adapter) => adapter.GetIPProperties().GetIPv4Properties()?.Index ?? 0;

    internal static int Index(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return 0;
        return GetBestInterface(BitConverter.ToUInt32(address.GetAddressBytes()), out var index) == 0 ? index : 0;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint address, out int index);
}
