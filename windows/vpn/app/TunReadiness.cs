using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Pomogator.Vpn;

internal sealed record TunObservation(Guid Id, string Alias, OperationalStatus? Status = null, int Index = 0,
    string? AddressState = null, bool AddressReady = false, int PeerRoute = 0, int ResolverRoute = 0,
    int InternetRoute = 0, int Uplink = 0, string[]? Dns = null, string? ExpectedDns = null, string[]? Ipv4 = null,
    string? ErrorCode = null, int WaitedMilliseconds = 0)
{
    internal bool DnsReady => ExpectedDns != null && Dns?.Contains(ExpectedDns) == true;
    internal bool Ready => Failure == null;

    internal string? Failure => ErrorCode == "TUN_XRAY_EXITED" ? "Ядро TUN завершилось во время ожидания Wintun." :
        ErrorCode == "TUN_NO_SNAPSHOT" ? "Ожидание Wintun не дошло до снимка параметров Windows." :
        ErrorCode != null ? "Windows не прочитала параметры Wintun. Код " + ErrorCode + "." :
        Status == null ? "Windows не показала ожидаемый интерфейс Wintun." :
        Status != OperationalStatus.Up || Index <= 0 ? "Интерфейс Wintun появился, но ещё недоступен для IPv4." :
        !AddressReady ? "Windows не подтвердила готовность IPv4-адреса Wintun." :
        PeerRoute != Uplink || PeerRoute == Index || Uplink <= 0 ? "Исходный маршрут к VPN-серверу не подтверждён." :
        ResolverRoute != Index ? "Windows не направляет контрольный DNS через Wintun." :
        InternetRoute != Index ? "Windows не направляет контрольный HTTPS-адрес через Wintun." :
        !DnsReady ? "Windows не подтвердила назначение DNS-сервера интерфейсу Wintun." : null;

    internal IEnumerable<string> Lines()
    {
        yield return $"Wintun: {Alias}; GUID={Id:D}; индекс IPv4={FormatIndex(Index)}; состояние={Status?.ToString() ?? "не обнаружен"}";
        yield return $"IPv4 Wintun: {List(Ipv4)}; ожидается {TunConfig.Address}/{TunConfig.PrefixLength}; " +
            $"состояние ожидаемого адреса: {AddressState ?? "не найден"}";
        yield return $"Выбранные Windows маршруты: VPN-сервер IF {FormatIndex(PeerRoute)} (ожидается {FormatIndex(Uplink)}), " +
            $"DNS IF {FormatIndex(ResolverRoute)}, публичный IPv4 IF {FormatIndex(InternetRoute)} (ожидается {FormatIndex(Index)})";
        yield return $"DNS на Wintun: ожидается {ExpectedDns ?? "не определён"}; получен {List(Dns)}";
        yield return $"Ожидание готовности Wintun: {(ErrorCode == "TUN_NO_SNAPSHOT" ? "нет результата" : WaitedMilliseconds + " мс")}";
        if (Failure is { } failure) yield return failure;
    }

    private static string List(string[]? values) => values is { Length: > 0 } ? string.Join(", ", values) : "не подтверждено";
    private static string FormatIndex(int value) => value > 0 ? value.ToString() : "не подтверждён";
}

internal static class TunReadiness
{
    internal static async Task<TunObservation> Wait(Func<TunObservation> read, Func<bool> running,
        TimeSpan timeout, TimeSpan interval, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var observation = read() with { WaitedMilliseconds = (int)timer.ElapsedMilliseconds };
            if (!running()) return observation with { ErrorCode = "TUN_XRAY_EXITED" };
            var remaining = timeout - timer.Elapsed;
            if (observation.Ready || remaining <= TimeSpan.Zero) return observation;
            await Task.Delay(remaining < interval ? remaining : interval, token);
        }
    }
}
