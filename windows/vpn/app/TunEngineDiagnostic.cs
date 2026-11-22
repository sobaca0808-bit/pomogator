namespace Pomogator.Vpn;

internal sealed class TunEngineDiagnostic
{
    private string? detail;
    private int? exitCode;

    internal void Read(string line)
    {
        var known = line.Contains("unable to set routes", StringComparison.OrdinalIgnoreCase) ? "установка маршрутов" :
            line.Contains("unable to set ips", StringComparison.OrdinalIgnoreCase) ? "назначение IPv4" :
            line.Contains("unable to set DNS", StringComparison.OrdinalIgnoreCase) ? "назначение DNS" :
            line.Contains("unable to set metric and MTU", StringComparison.OrdinalIgnoreCase) ? "назначение метрики и MTU" :
            line.Contains("unable to create", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ? "создание адаптера Wintun" : null;
        if (known != null) Volatile.Write(ref detail, known);
    }

    internal void Exited(int code) => exitCode = code;

    internal string? Description
    {
        get
        {
            var stage = Volatile.Read(ref detail);
            return (exitCode, stage) switch
            {
                (int code, not null) => $"Xray TUN завершился с кодом {code}. Последняя распознанная ошибка ядра: {stage}.",
                (int code, null) => $"Xray TUN завершился с кодом {code}. Причина завершения ядром не подтверждена.",
                (null, not null) => $"Последняя распознанная ошибка Xray TUN: {stage}.",
                _ => null
            };
        }
    }
}
