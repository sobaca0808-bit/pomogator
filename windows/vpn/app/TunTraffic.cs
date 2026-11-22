using System.IO;

namespace Pomogator.Vpn;

internal sealed record TunTrafficSample(TunPacketSample? Windows, TunXraySample? Xray)
{
    internal static async Task<TunTrafficSample> Capture(Guid id, int port, int pid, CancellationToken token)
    {
        var windows = TunPacketCounters.Read(id);
        var xray = await TunXrayCounters.Read(Path.Combine(AppContext.BaseDirectory, "xray.exe"), port, pid, token);
        return new TunTrafficSample(windows, xray);
    }

    internal IEnumerable<string> Describe(string phase, TunTrafficSample earlier, bool explainFailure = true)
    {
        var sent = Difference(Windows?.SentByWindows, earlier.Windows?.SentByWindows);
        var received = Difference(Windows?.ReceivedByWindows, earlier.Windows?.ReceivedByWindows);
        var tunUp = Difference(Xray?.InboundUplink, earlier.Xray?.InboundUplink);
        var tunDown = Difference(Xray?.InboundDownlink, earlier.Xray?.InboundDownlink);
        var vpnUp = Difference(Xray?.OutboundUplink, earlier.Xray?.OutboundUplink);
        var vpnDown = Difference(Xray?.OutboundDownlink, earlier.Xray?.OutboundDownlink);
        yield return $"{phase} · пакеты Windows → Wintun: {Format(sent)}, Wintun → Windows: {Format(received)}; " +
            $"отброшено TX/RX: {Format(Difference(Windows?.OutputDiscards, earlier.Windows?.OutputDiscards))}/" +
            $"{Format(Difference(Windows?.InputDiscards, earlier.Windows?.InputDiscards))}; " +
            $"ошибки TX/RX: {Format(Difference(Windows?.OutputErrors, earlier.Windows?.OutputErrors))}/" +
            $"{Format(Difference(Windows?.InputErrors, earlier.Windows?.InputErrors))}.";
        yield return $"{phase} · байты полезной нагрузки Xray TUN ↑/↓: {Format(tunUp)}/{Format(tunDown)}; " +
            $"байты транспорта VLESS ↑/↓: {Format(vpnUp)}/{Format(vpnDown)}.";
        if (!explainFailure) yield break;
        var indication = sent == 0 ? "Windows не зарегистрировала исходящие пакеты на Wintun." :
            tunUp == 0 && sent > 0 ? "Wintun видел исходящие пакеты, полезная нагрузка на входе Xray не зарегистрирована." :
            vpnUp == 0 && tunUp > 0 ? "Xray принял полезную нагрузку TUN, передача через VLESS не зарегистрирована." :
            vpnDown == 0 && vpnUp > 0 ? "Транспорт VLESS передавал данные, ответа от транспорта не зарегистрировано." :
            tunDown == 0 && vpnDown > 0 ? "Транспорт VLESS получил данные, возврат полезной нагрузки в TUN не зарегистрирован." :
            received == 0 && tunDown > 0 ? "Xray зарегистрировал ответ TUN, Windows не зарегистрировала входящие пакеты." : null;
        if (indication != null) yield return $"{phase} · по счётчикам: {indication}";
    }

    private static ulong? Difference(ulong? after, ulong? before) => after >= before ? after - before : null;
    private static long? Difference(long? after, long? before) => after >= before ? after - before : null;
    private static string Format<T>(T? value) where T : struct => value?.ToString() ?? "н/д";
}
