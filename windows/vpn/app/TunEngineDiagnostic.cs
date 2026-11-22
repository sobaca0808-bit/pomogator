namespace Pomogator.Vpn;

internal sealed class TunEngineDiagnostic
{
    private string? detail;
    private int? exitCode;
    private int started;
    internal bool Started => Volatile.Read(ref started) != 0;
    private readonly int[] counters = new int[7];
    private static readonly string[] Labels = [
        "маршрут контрольной пробы выбрал VLESS", "маршрут контрольной пробы отклонён",
        "сбой исходящего подключения", "TLS или REALITY внешнего соединения",
        "сбой DNS-over-HTTPS", "сбой чтения или передачи VLESS", "исходящий сбой без уточнённой стадии"
    ];

    internal string[] TakeEvents()
    {
        var result = new List<string>();
        for (var i = 0; i < counters.Length; i++)
        {
            var count = Interlocked.Exchange(ref counters[i], 0);
            if (count > 0) result.Add(Labels[i] + " × " + count);
        }
        return result.ToArray();
    }

    internal void Read(string line)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"Xray \d+\.\d+\.\d+ started\s*$"))
            Interlocked.Exchange(ref started, 1);
        var known = line.Contains("unable to set routes", StringComparison.OrdinalIgnoreCase) ? "установка маршрутов" :
            line.Contains("unable to set ips", StringComparison.OrdinalIgnoreCase) ? "назначение IPv4" :
            line.Contains("unable to set DNS", StringComparison.OrdinalIgnoreCase) ? "назначение DNS" :
            line.Contains("unable to set metric and MTU", StringComparison.OrdinalIgnoreCase) ? "назначение метрики и MTU" :
            line.Contains("unable to create", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ? "создание адаптера Wintun" : null;
        if (known != null) Volatile.Write(ref detail, known);

        bool Has(string fragment) => line.Contains(fragment, StringComparison.OrdinalIgnoreCase);
        if (Has("Hit route rule: [pmt-control-vless]")) Interlocked.Increment(ref counters[0]);
        if (Has("Hit route rule: [pmt-control-block]")) Interlocked.Increment(ref counters[1]);
        if (Has("failed to dial to dest") || Has("network is unreachable") || Has("no route to host"))
            Interlocked.Increment(ref counters[2]);
        if (Has("REALITY:") || Has("x509:") || Has("tls:") || Has("found outer tls version"))
            Interlocked.Increment(ref counters[3]);
        if (Has("failed to retrieve response for") || Has("failed to handle DOH response") || Has("failed to lookup IP"))
            Interlocked.Increment(ref counters[4]);
        if (Has("failed to decode response header") || Has("failed to transfer request payload") ||
            Has("failed to transfer response payload"))
            Interlocked.Increment(ref counters[5]);
        if (Has("failed to process outbound traffic") || Has("failed to find an available destination"))
            Interlocked.Increment(ref counters[6]);
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
