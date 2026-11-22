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

public sealed record ProxyRecovery(ProxyState Before, ProxyState Applied);

public enum RestoreResult { Restored, AlreadyRestored, ChangedElsewhere }

public interface IProxySettings
{
    ProxyState Read();
    void Write(ProxyState state);
}

public static class ProxyControl
{
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
