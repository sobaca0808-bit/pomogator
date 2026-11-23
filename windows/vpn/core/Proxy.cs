using System.Net;
using System.Text.RegularExpressions;

namespace Pomogator.Vpn;

public sealed record ProxyState(uint Flags, string Server, string Bypass, string Pac)
{
    public const uint Direct = 1;
    public const uint ExplicitProxy = 2;
    public const uint AutomaticScript = 4;
    public const uint AutoDetect = 8;

    public bool InUse => (Flags & (ExplicitProxy | AutomaticScript)) != 0 || Pac.Length != 0;

    internal ProxyState ForLoopback(int port) => this with { Flags = Direct | ExplicitProxy, Server = $"127.0.0.1:{port}" };
}

public sealed record ProxyRecovery(ProxyState Before, ProxyState Applied, string? Connection = null);

public enum RestoreResult { Restored, AlreadyRestored, ChangedElsewhere }

public interface IProxySettings
{
    ProxyState Read();
    void Write(ProxyState state);
}

public static class ProxyControl
{
    public static WebProxy VerifiedWindowsProxy(ProxyState effective, int port)
    {
        var server = $"127.0.0.1:{port}";
        var configured = effective.Server.Trim();
        var https = configured.Contains('=')
            ? configured.Split(';', StringSplitOptions.TrimEntries)
                .Where(item => item.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                .Select(item => item[6..]).ToArray()
            : new[] { configured };
        if ((effective.Flags & ProxyState.ExplicitProxy) == 0 ||
            (effective.Flags & (ProxyState.AutoDetect | ProxyState.AutomaticScript)) != 0 ||
            effective.Pac.Length != 0 || https.Length != 1 ||
            !(https[0] == server || https[0] == "http://" + server))
            throw new VpnError("Windows не подтвердила наш прокси для активного подключения.", Check.Https);
        var bypass = effective.Bypass.Split([';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return new WebProxy("http://" + server) {
            BypassProxyOnLocal = bypass.Contains("<local>", StringComparer.OrdinalIgnoreCase),
            BypassList = bypass.Where(item => !item.Equals("<local>", StringComparison.OrdinalIgnoreCase))
                .Select(item => "^" + (item.Contains("://") ? "" : "[^:]+://") +
                    Regex.Escape(item).Replace(@"\*", ".*") + @"(?::\d+)?$").ToArray()
        };
    }

    public static ProxyRecovery Prepare(IProxySettings settings, int port)
    {
        var before = settings.Read();
        if (before.InUse)
            throw new VpnError("В Windows уже включён прокси или автоматический сценарий. Оставьте локальный режим или освободите системный прокси в другом приложении.");
        return new ProxyRecovery(before, before.ForLoopback(port));
    }

    // The caller holds an exclusive per-user lock and has saved an encrypted recovery record.
    public static void Apply(IProxySettings settings, ProxyRecovery recovery)
    {
        if (settings.Read() != recovery.Before)
            throw new VpnError("Настройки прокси изменились после проверки. Повторите подключение.");
        settings.Write(recovery.Applied);
        if (settings.Read() != recovery.Applied)
            throw new VpnError("Windows не подтвердила настройки системного прокси. Проверьте параметры Windows.");
    }

    public static RestoreResult Restore(IProxySettings settings, ProxyRecovery recovery)
    {
        var current = settings.Read();
        if (current == recovery.Before) return RestoreResult.AlreadyRestored;
        if (current != recovery.Applied) return RestoreResult.ChangedElsewhere;
        settings.Write(recovery.Before);
        if (settings.Read() != recovery.Before)
            throw new VpnError("Windows не подтвердила восстановление прокси. Сохранена запись восстановления.");
        return RestoreResult.Restored;
    }
}
