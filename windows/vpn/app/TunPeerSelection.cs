using System.Net;
using System.Net.Sockets;

namespace Pomogator.Vpn;

internal sealed class NoVerifiedPeer(string message, Check? failedCheck = null) : Exception(message)
{
    internal Check? FailedCheck { get; } = failedCheck;
}

internal sealed record VerifiedPeer(Xray Core, IPAddress Address, TunRecovery Recovery, TunProbe Baseline);

internal static class TunPeerSelection
{
    internal static async Task<VerifiedPeer> Open(Profile profile, string executable, Action<string> log,
        CancellationToken token)
    {
        IPAddress[] addresses;
        using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            lookup.CancelAfter(TimeSpan.FromSeconds(5));
            try { addresses = await Dns.GetHostAddressesAsync(profile.Host, AddressFamily.InterNetwork, lookup.Token); }
            catch (SocketException error)
            {
                throw new NoVerifiedPeer($"DNS сервера до TUN: WinSock {error.NativeErrorCode}.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new NoVerifiedPeer("До TUN истекло время разрешения IPv4 сервера."); }
        }

        return await TryCandidates(addresses, async (address, deadline) =>
        {
            var before = TunWindows.Capture(address);
            var pinned = profile with { Host = address.ToString() };
            var (latency, source) = await Xray.CheckServerSourceAsync(pinned, deadline);
            if (!before.HasSource(source))
                throw new VpnError("Источник соединения не принадлежит выбранному интерфейсу.");
            log(TunEgress.UsesQuic(profile) ? "Windows выбрала источник для UDP. Ожидается HTTPS через QUIC." :
                $"До TUN TCP сервера подтверждён за {latency.TotalMilliseconds:F0} мс.");
            var uplink = TunWindows.UplinkName(before, source);
            Xray? temporary = null;
            try
            {
                temporary = await Xray.StartWithPortsAsync(executable, () =>
                {
                    var port = Xray.FreePort();
                    return (TunConfig.PreflightEngine(address, source, uplink, profile, port), port, Array.Empty<int>());
                }, deadline, WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback,
                    startupExit: code => log($"До TUN контрольный Xray завершился с кодом {code}."));
                log("До TUN процесс Xray запущен и владеет локальным HTTP-входом.");
                var probe = await TunConnectivity.CheckBeforeTun(temporary.Port, temporary.ProcessId, deadline);
                log(probe.Line("До TUN · HTTPS по IPv4 через VLESS"));
                if (!probe.Success)
                {
                    using var client = Xray.ThroughProxy(temporary.Port);
                    if (await InternetHealth.ProbeAsync(client, deadline, log) == null)
                        throw new VpnError("До TUN ни один HTTPS-адрес через VLESS не ответил.", Check.Https);
                    log("До TUN альтернативный HTTPS-адрес подтвердил VLESS.");
                }
                deadline.ThrowIfCancellationRequested();
                var record = new TunRecovery(Guid.NewGuid(), address.ToString(),
                    before with { SourceIpv4 = source.ToString() }, TunConfig.Routes(address));
                var selected = new VerifiedPeer(temporary, address, record, probe);
                temporary = null; // ownership passes only after a successful HTTPS probe
                return selected;
            }
            finally { temporary?.Dispose(); }
        }, log, token);
    }

    internal static async Task<T> TryCandidates<T>(IEnumerable<IPAddress> addresses,
        Func<IPAddress, CancellationToken, Task<T>> verify, Action<string> log, CancellationToken token)
    {
        var candidates = addresses.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast) &&
            ip.ToString() != TunConfig.Address && ip.ToString() != TunConfig.Resolver &&
            ip.ToString() != TunConfig.InternetProbe).Distinct().Take(3).ToArray();
        Check? lastStage = null;
        for (var i = 0; i < candidates.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            lastStage = null;
            log($"До TUN · проверка IPv4 {i + 1} из {candidates.Length}.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try { return await verify(candidates[i], deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { log("Истекло время проверки этого IPv4."); }
            catch (VpnError error) { lastStage = error.FailedCheck; log(error.Message); }
            catch (SocketException error) { log($"WinSock {error.NativeErrorCode} при проверке адреса."); }
        }
        token.ThrowIfCancellationRequested();
        throw new NoVerifiedPeer(candidates.Length == 0 ? "Нет подходящих IPv4-адресов сервера." :
            "До включения TUN HTTPS через выбранный профиль не подтверждён. См. этапы проверок в журнале.", lastStage);
    }
}
