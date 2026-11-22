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

internal sealed record TunBaseline(int Uplink, string UplinkId, string[] Addresses, string[] Dns, int InternetRoute, int? PublicRoute = null);
internal sealed record TunRecovery(Guid Id, string Peer, TunBaseline Before, string[]? InstalledRoutes = null,
    string InstalledDns = TunConfig.Resolver);

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
            Index(IPAddress.Parse(TunConfig.Resolver)), Index(IPAddress.Parse(TunConfig.InternetProbe)));
    }

    internal static bool Same(TunBaseline expected, TunBaseline actual) =>
        expected.Uplink == actual.Uplink && expected.UplinkId == actual.UplinkId &&
        expected.Addresses.SequenceEqual(actual.Addresses) && expected.Dns.SequenceEqual(actual.Dns);

    internal static bool MatchesBaseline(TunBaseline expected, TunBaseline actual) =>
        Same(expected, actual) && expected.InternetRoute == actual.InternetRoute &&
        (expected.PublicRoute == null || expected.PublicRoute == actual.PublicRoute);

    internal static NetworkInterface? Find(Guid id) => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
        adapter => adapter.Name == TunConfig.Adapter(id) && Guid.TryParse(adapter.Id, out var guid) && guid == TunConfig.AdapterId(id));

    internal static TunObservation Observe(TunRecovery record, IPAddress? publicProbe = null)
    {
        var observation = new TunObservation(TunConfig.AdapterId(record.Id), TunConfig.Adapter(record.Id),
            Uplink: record.Before.Uplink, ExpectedDns: record.InstalledDns);
        try
        {
            var adapter = Find(record.Id);
            if (adapter == null) return observation;
            observation = observation with { Status = adapter.OperationalStatus };
            var properties = adapter.GetIPProperties();
            var index = properties.GetIPv4Properties()?.Index ?? 0;
            var addresses = properties.UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
            var address = addresses.FirstOrDefault(item => item.Address.ToString() == TunConfig.Address);
            observation = observation with { Index = index,
                Ipv4 = addresses.Select(item => item.Address + "/" + item.PrefixLength).ToArray(), AddressState = address == null ? null :
                $"/{address.PrefixLength}, {address.DuplicateAddressDetectionState}",
                AddressReady = address is { PrefixLength: TunConfig.PrefixLength, DuplicateAddressDetectionState: DuplicateAddressDetectionState.Preferred } };
            observation = observation with { PeerRoute = Index(IPAddress.Parse(record.Peer)),
                ResolverRoute = Index(IPAddress.Parse(record.InstalledDns)),
                InternetRoute = Index(publicProbe ?? IPAddress.Parse(TunConfig.InternetProbe)) };
            return observation with { Dns = properties.DnsAddresses.Select(ip => ip.ToString()).ToArray() };
        }
        catch (Exception error) when (error is NetworkInformationException or SocketException)
        {
            return observation with { ErrorCode = "0x" + error.HResult.ToString("X8") };
        }
    }

    internal static Socket OpenSocket(int index, IPAddress target, SocketType type, ProtocolType protocol)
    {
        if (index <= 0 || Index(target) != index)
            throw new SocketException((int)SocketError.NetworkUnreachable);
        var socket = new Socket(AddressFamily.InterNetwork, type, protocol);
        try
        {
            // IP_UNICAST_IF (31) takes a Windows IPv4 interface index in network byte order.
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(index));
            socket.Bind(new IPEndPoint(IPAddress.Parse(TunConfig.Address), 0));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    internal static HttpClient Http(Guid id, RemoteCertificateValidationCallback? validation = null,
        string? requiredHost = null, IPAddress? pinnedAddress = null)
    {
        var adapter = Find(id) ?? throw new VpnError("Интерфейс TUN пропал.");
        var adapterIndex = Index(adapter);
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        if (validation != null) handler.SslOptions.RemoteCertificateValidationCallback = validation;
        handler.ConnectCallback = async (context, token) =>
        {
            var addresses = pinnedAddress != null && string.Equals(requiredHost, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase)
                ? new[] { pinnedAddress } : IPAddress.TryParse(context.DnsEndPoint.Host, out var literal) &&
                literal.AddressFamily == AddressFamily.InterNetwork ? new[] { literal } :
                await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
            foreach (var ip in addresses)
            {
                Socket? socket = null;
                try
                {
                    // Windows still performs TLS validation for the original host.
                    socket = OpenSocket(adapterIndex, ip, SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(ip, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket?.Dispose(); }
                catch { socket?.Dispose(); throw; }
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

    internal static bool Clean(TunRecovery record)
    {
        if (Find(record.Id) != null) return false;
        using var registry = Registry.LocalMachine.OpenSubKey(ConnectionKey(record.Id));
        return registry == null;
    }

    internal static async Task<bool> Recover(TunRecovery record, Action<string>? diagnostic = null)
    {
        if (Clean(record)) return true;
        string? alias;
        string? pnp;
        using (var registry = Registry.LocalMachine.OpenSubKey(ConnectionKey(record.Id)))
        {
            alias = registry?.GetValue("Name") as string;
            pnp = registry?.GetValue("PnpInstanceID") as string;
        }
        if (pnp == null || !(pnp.StartsWith("SWD\\WINTUN\\", StringComparison.OrdinalIgnoreCase) ||
            pnp.StartsWith("ROOT\\WINTUN\\", StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(alias, TunConfig.Adapter(record.Id), StringComparison.Ordinal))
            return Refuse("unrecognized-device-instance");

        var adapter = Find(record.Id);
        if (adapter == null)
        {
            // After a hard crash a non-present Wintun can remain registered while IP Helper has no interface.
            // Verify it is non-present via PnP before removing the exact owned devnode.
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(pnp));
            var command = "$ErrorActionPreference='Stop'; $instance=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
                encoded + "')); $devices=@(Get-PnpDevice -Class Net -PresentOnly | Where-Object InstanceId -eq $instance); $devices.Count";
            var present = await PowerShell(command);
            if (present?.Exit != 0 || present.Value.Output.Trim() != "0")
                return Refuse("device-visible-to-pnp-only");
        }
        else
        {
            var props = adapter.GetIPProperties();
            if (props.UnicastAddresses.Any(a => !a.Address.IsIPv6LinkLocal && a.Address.ToString() != TunConfig.Address &&
                    !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) ||
                props.DnsAddresses.Any(a => a.ToString() != record.InstalledDns)) return Refuse("unrecognized-address-or-dns");
            var routes = await ReadRoutes(Index(adapter));
            if (routes == null) return Refuse("could-not-read-routes");
            var planned = (record.InstalledRoutes ?? TunConfig.Routes(IPAddress.Parse(record.Peer))).ToHashSet();
            if (routes.Any(route => !OwnRoute(route, planned))) return Refuse("unrecognized-route");
        }
        var result = await Run(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), "/remove-device", pnp);
        if (result?.Exit != 0) return Refuse("device-remove-failed");
        return await WaitClean(record, TimeSpan.FromSeconds(5)) || Refuse("device-still-present");

        bool Refuse(string reason)
        {
            diagnostic?.Invoke(reason);
            return false;
        }
    }

    internal static bool OwnRoute(string row, IReadOnlySet<string> planned)
    {
        var parts = row.Split('|');
        if (parts.Length != 3 || parts[1] is not ("0.0.0.0" or "::") ||
            !int.TryParse(parts[2], out var metric)) return false;
        if (planned.Contains(parts[0])) return metric == 0;
        return parts[0] is TunConfig.Address + "/32" or "198.18.240.0/30" or "198.18.240.3/32" or
            "255.255.255.255/32" or "224.0.0.0/4" or "ff00::/8" or "fe80::/64" or "169.254.0.0/16" ||
            parts[0].StartsWith("fe80:", StringComparison.OrdinalIgnoreCase) && parts[0].EndsWith("/128", StringComparison.Ordinal);
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
        var command = "$ErrorActionPreference='Stop'; Get-NetRoute -InterfaceIndex " + index + " | ForEach-Object { $_.DestinationPrefix + '|' + $_.NextHop + '|' + $_.RouteMetric }";
        var result = await PowerShell(command);
        return result?.Exit == 0 ? result.Value.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
    }

    private static Task<(int Exit, string Output)?> PowerShell(string command) =>
        Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

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
