using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Pomogator.Vpn;

internal sealed class SingBoxDiagnostic(IPAddress peer, int port)
{
    private readonly Regex peerEndpoint = new(@"(?<![\d.])" + Regex.Escape(peer.ToString()) + ":" +
        port.ToString(CultureInfo.InvariantCulture) + @"(?!\d)", RegexOptions.CultureInvariant);
    private readonly object sync = new();
    private readonly Dictionary<int, int> counts = new();
    private int started;
    internal bool Started => Volatile.Read(ref started) != 0;
    private DateTimeOffset? firstDenial;
    private DateTimeOffset? lastDenial;
    private static readonly (int Code, string Pattern)[] SocketErrors = [
        (10013, @"\b10013\b|forbidden by its access permissions|запрещенн(?:ым|ые) правами доступа|запрещённ(?:ым|ые) правами доступа"),
        (10053, @"\b10053\b|aborted by the software|прервано программным обеспечением"),
        (10054, @"\b10054\b|forcibly closed by the remote host|принудительно разорвано удал[её]нным хостом")
    ];

    internal void Read(string line)
    {
        if (line.Contains("sing-box started", StringComparison.OrdinalIgnoreCase)) Volatile.Write(ref started, 1);
        if (!peerEndpoint.IsMatch(line) || !line.Contains("connectex", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var (code, pattern) in SocketErrors)
        {
            if (!Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            lock (sync)
            {
                counts[code] = counts.GetValueOrDefault(code) + 1;
                if (code == 10013) { lastDenial = DateTimeOffset.UtcNow; firstDenial ??= lastDenial; }
            }
            return;
        }
    }

    internal string[] Take(out TunDialDenial[] denied)
    {
        lock (sync)
        {
            denied = firstDenial is { } first && lastDenial is { } last
                ? [new(0, 0, 0, first.AddSeconds(-6), last)] : [];
            firstDenial = lastDenial = null;
            var lines = counts.OrderBy(item => item.Key)
                .Select(item => $"TCP к VPN-серверу · ConnectEx · WinSock {item.Key} · отказов {item.Value}.").ToArray();
            counts.Clear();
            return lines;
        }
    }
}
