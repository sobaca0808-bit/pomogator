using System.Net;

namespace Pomogator.Vpn;

internal sealed class TunEngineDiagnostic(IPAddress? peer = null, int port = 0, bool rawTransport = false)
{
    private string? detail;
    private int? exitCode;
    private int started;
    internal bool Started => Volatile.Read(ref started) != 0;
    private static readonly string[] Labels = [
        "маршрут контрольной пробы выбрал VLESS", "маршрут контрольной пробы отклонён",
        "сбой открытия исходящего сокета", "TLS или REALITY внешнего соединения",
        "сбой DNS-over-HTTPS", "сбой чтения или передачи VLESS", "исходящий сбой без уточнённой стадии",
        "TUN перехватил трафик к VPN-серверу, повторная отправка заблокирована",
        "открыт защищённый транспорт VLESS"
    ];
    private readonly int[] counters = new int[Labels.Length];
    private readonly TunSocketDiagnostic? sockets = peer == null ? null : new(peer, port, rawTransport);

    internal string[] TakeEvents()
    {
        var result = new List<string>();
        for (var i = 0; i < counters.Length; i++)
        {
            var count = Interlocked.Exchange(ref counters[i], 0);
            if (count > 0) result.Add(Labels[i] + " × " + count);
        }
        if (sockets != null) result.AddRange(sockets.TakeEvents());
        return result.ToArray();
    }

    internal void Read(string line)
    {
        sockets?.Read(line);
        if (line.Contains("core: Xray ", StringComparison.Ordinal) && line.EndsWith(" started", StringComparison.Ordinal))
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
        if (Has("transport/internet/tcp: failed to dial to dest"))
            Interlocked.Increment(ref counters[2]);
        if (Has("app/proxyman/outbound: failed to process outbound traffic") &&
            (Has("REALITY:") || Has("x509:") || Has("tls:")))
            Interlocked.Increment(ref counters[3]);
        if (Has("failed to retrieve response for") || Has("failed to handle DOH response") || Has("failed to lookup IP"))
            Interlocked.Increment(ref counters[4]);
        if (Has("failed to decode response header") || Has("failed to transfer request payload") ||
            Has("failed to transfer response payload"))
            Interlocked.Increment(ref counters[5]);
        if (Has("failed to process outbound traffic") || Has("failed to find an available destination"))
            Interlocked.Increment(ref counters[6]);
        if (Has("Hit route rule: [pmt-peer-reentry]")) Interlocked.Increment(ref counters[7]);
        if (Has("proxy/vless/outbound: tunneling request to")) Interlocked.Increment(ref counters[8]);
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
