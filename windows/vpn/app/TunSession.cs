using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Pomogator.Vpn;

internal sealed record TunRequest(string Peer, Profile? Profile, bool Recover, TunBaseline Before);
internal sealed record TunReply(bool Success, string Message, TunObservation? Observation = null, string? EngineNote = null,
    int DiagnosticsPort = 0, int EnginePid = 0, TunControlEndpoint? Control = null,
    int Sequence = 0, string[]? Events = null, TunReference? Reference = null);

internal sealed record TunReference(TunProbe Before, TunProbe? After = null);

internal sealed class TunSession(TunRecovery record) : IDisposable
{
    private Process? helper;
    private NamedPipeServerStream? pipe;
    private Task<string?>? stopNotice;
    private int sequence;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string[]?>> pendingSamples = new();
    internal TunObservation? Observation { get; private set; }
    internal string? EngineNote { get; private set; }
    internal int DiagnosticsPort { get; private set; }
    internal int EnginePid { get; private set; }
    internal int PeerPort { get; private set; }
    internal TunControlEndpoint? Control { get; private set; }
    internal TunReference? Reference { get; private set; }
    internal TunRecovery Record { get; } = record;
    internal bool Running => helper is { HasExited: false };

    private static string PipeName(Guid id) => "PomogatorTun-" + id.ToString("N");

    internal async Task StartAsync(Profile profile, CancellationToken token)
    {
        PeerPort = profile.Port;
        if (TunWindows.OtherVpn()) throw new VpnError("Найден другой VPN или UBoost. Выберите локальный прокси либо завершите другой VPN самостоятельно.");
        if (File.Exists(LocalData.TunRecovery) || !TunWindows.Vacant(Record)) throw new VpnError("Имя или идентификатор интерфейса TUN уже занят.");
        LocalData.Save(LocalData.TunRecovery, Record);
        await Exchange(new TunRequest(Record.Peer, profile with { Full = null }, false, Record.Before), token);
    }

    internal async Task RestoreAsync(CancellationToken token)
    {
        if (!TunWindows.Clean(Record))
        {
            await Exchange(new TunRequest(Record.Peer, null, true, Record.Before), token);
            if (!TunWindows.Clean(Record)) throw new VpnError("Windows не подтвердила удаление интерфейса TUN.");
        }
        File.Delete(LocalData.TunRecovery);
    }

    private async Task Exchange(TunRequest request, CancellationToken token)
    {
        pipe = new NamedPipeServerStream(PipeName(Record.Id), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--tun-helper");
        start.ArgumentList.Add(Record.Id.ToString("N"));
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            helper = await Task.Run(() => Process.Start(start), token) ?? throw new VpnError("Помощник TUN не запустился.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new VpnError("Запрос прав администратора для TUN отменён."); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(68));
        try
        {
            var pending = pipe.WaitForConnectionAsync(deadline.Token);
            if (await Task.WhenAny(pending, helper.WaitForExitAsync(deadline.Token)) != pending && !pipe.IsConnected)
                throw new VpnError("Помощник TUN завершился. Повышение прав требуется под той же учётной записью Windows.");
            await pending;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != helper.Id)
                throw new VpnError("Windows не подтвердила личность помощника TUN.");
            await Send(pipe, request, deadline.Token);
            var reply = await Read<TunReply>(pipe, deadline.Token);
            Observation = reply.Observation;
            EngineNote = reply.EngineNote;
            DiagnosticsPort = reply.DiagnosticsPort;
            EnginePid = reply.EnginePid;
            Control = reply.Control;
            Reference = reply.Reference;
            if (!reply.Success) throw new VpnError(reply.Message);
            if (!request.Recover) stopNotice = ReadStop(pipe);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new VpnError("Помощник TUN не ответил за 68 секунд."); }
    }

    internal async Task<string[]?> SampleEvents(CancellationToken token)
    {
        if (pipe is not { IsConnected: true } channel) return null;
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<string[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingSamples.TryAdd(id, completion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await Send(channel, id, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { return null; }
        finally { pendingSamples.TryRemove(id, out _); }
    }

    private async Task<string?> ReadStop(Stream channel)
    {
        try
        {
            while (true)
            {
                var reply = await Read<TunReply>(channel, CancellationToken.None);
                if (reply.Sequence == 0) return reply.EngineNote;
                if (pendingSamples.TryRemove(reply.Sequence, out var sample)) sample.TrySetResult(reply.Events);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or JsonException)
        { return null; }
        finally
        {
            foreach (var sample in pendingSamples.Values) sample.TrySetResult(null);
        }
    }

    internal async Task<string?> ReadEngineStop()
    {
        if (stopNotice is { } pending && await Task.WhenAny(pending, Task.Delay(250)) == pending)
            return await pending ?? EngineNote;
        return EngineNote;
    }

    public void Dispose()
    {
        pipe?.Dispose(); // EOF also stops the helper if the UI process was terminated.
        pipe = null;
        if (helper is { } current)
        {
            try { current.WaitForExit(12000); }
            finally { current.Dispose(); helper = null; }
        }
    }

    internal static async Task<int> RunWorker(Guid id, int parent)
    {
        if (!TunWindows.Administrator() || !TunWindows.SupportsWindows) return 1;
        using var channel = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var boot = new CancellationTokenSource(TimeSpan.FromSeconds(64));
        TunRecovery? record = null;
        Xray? engine = null;
        Xray? referenceEngine = null;
        TunConsole? console = null;
        TunObservation? observation = null;
        bool launchingTun = false;
        var diagnostic = new TunEngineDiagnostic();
        try
        {
            await channel.ConnectAsync(boot.Token);
            if (!GetNamedPipeServerProcessId(channel.SafePipeHandle, out var server) || server != parent) return 1;
            using (var caller = Process.GetProcessById(parent))
                if (!string.Equals(caller.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 1;
            var request = await Read<TunRequest>(channel, boot.Token);
            if (!IPAddress.TryParse(request.Peer, out var peer) || peer.AddressFamily != AddressFamily.InterNetwork) return 1;
            record = LocalData.Read<TunRecovery>(LocalData.TunRecovery);
            if (record == null || record.Id != id || record.Peer != peer.ToString() ||
                !TunWindows.MatchesBaseline(record.Before, request.Before) || record.InstalledDns != TunConfig.Resolver ||
                record.InstalledRoutes != null && !record.InstalledRoutes.SequenceEqual(TunConfig.Routes(peer))) return 1;
            if (request.Recover)
            {
                string? reason = null;
                var restored = await TunWindows.Recover(record, code => reason = code);
                await Send(channel, new TunReply(restored, "Не удалось безопасно убрать оставшийся интерфейс TUN. Запись восстановления сохранена. " +
                    (reason == null ? "" : "Диагностика: " + reason + ".")), boot.Token);
                return restored ? 0 : 1;
            }

            if (request.Profile is not { Full: null } profile ||
                profile.Security is not ("tls" or "reality") || profile.Id == Guid.Empty || profile.Port is < 1 or > 65535) return 1;
            diagnostic = new TunEngineDiagnostic(peer, profile.Port, profile.Transport == "raw");
            if (!IPAddress.TryParse(record.Before.SourceIpv4, out var source) || !record.Before.HasSource(source))
                throw new VpnError("Не подтверждён исходный IPv4 для подключения Xray. TUN не запущен.");
            if (!TunWindows.Vacant(record) || TunWindows.OtherVpn() || SystemProxy.AnyInUse() ||
                !TunWindows.MatchesBaseline(request.Before, TunWindows.Capture(peer)))
                throw new VpnError("Перед запуском изменился рабочий интерфейс или появился другой VPN. Настройки не применены.");
            var uplink = TunWindows.UplinkName(record.Before, source);
            // Parent EOF cancels the reference and the TUN boot. The reference uses the same
            // high-integrity token, executable, endpoint and outbound as the combined engine.
            using var alive = CancellationTokenSource.CreateLinkedTokenSource(boot.Token);
            var runtime = Read<int>(channel, CancellationToken.None);
            var image = Path.Combine(AppContext.BaseDirectory, "xray.exe");
            TunReference? reference = null;
            TunControlEndpoint? referenceEndpoint = null;
            try
            {
                int referencePort = Xray.FreePort();
                var endpoint = referenceEndpoint = PrivateControl(referencePort);
                var referenceStart = Xray.StartAsync(image,
                    TunConfig.ReferenceEngine(peer, source, uplink, profile, endpoint),
                    endpoint.Port, alive.Token, WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback, startupSeconds: 8);
                if (await Task.WhenAny(referenceStart, runtime) == runtime) alive.Cancel();
                referenceEngine = await referenceStart;
                var probe = TunConnectivity.CheckControl(endpoint, referenceEngine.ProcessId, alive.Token);
                if (await Task.WhenAny(probe, runtime) == runtime) alive.Cancel();
                reference = new(await probe);
            }
            catch (VpnError error)
            {
                reference = new(new(false, "не подтверждён запуск контрольного Xray · этап " +
                    (error.FailedCheck?.ToString() ?? "запуск"), 0));
            }

            alive.Token.ThrowIfCancellationRequested();
            if (!TunWindows.Vacant(record) || !TunWindows.MatchesBaseline(record.Before, TunWindows.Capture(peer)) ||
                TunWindows.UplinkName(record.Before, source) != uplink)
                throw new VpnError("Исходная сеть изменилась во время контрольной пробы. TUN не запущен.");
            console = new TunConsole();
            var port = Xray.FreePort();
            int diagnosticsPort;
            do { diagnosticsPort = Xray.FreePort(); } while (diagnosticsPort == port);
            int controlPort;
            do { controlPort = Xray.FreePort(); } while (controlPort == port || controlPort == diagnosticsPort);
            var control = PrivateControl(controlPort);
            launchingTun = true;
            var config = TunConfig.Engine(id, peer, source, uplink, profile, port, diagnosticsPort, control);
            var start = Xray.StartAsync(image, config, port,
                alive.Token, WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24,
                diagnosticLine: diagnostic.Read, startupExit: diagnostic.Exited);
            if (await Task.WhenAny(start, runtime) == runtime) alive.Cancel();
            engine = await start;
            // Xray starts tagged listeners in unspecified order. Its startup marker appears only
            // after the TUN handler has applied its filters and the other features have started.
            var wait = TunReadiness.Wait(() =>
            {
                var state = TunWindows.Observe(record);
                return observation = state.Ready && !diagnostic.Started ?
                    state with { ErrorCode = "TUN_ENGINE_NOT_READY" } : state;
            }, () => engine.Running,
                TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300), alive.Token);
            if (await Task.WhenAny(wait, runtime) == runtime) alive.Cancel();
            observation = await wait;
            if (observation.Failure is { } failure)
            {
                if (engine.ExitCode is { } code) diagnostic.Exited(code);
                throw new VpnError(failure);
            }
            if (reference is { Before.Success: true } && referenceEndpoint is { } bound && referenceEngine is { Running: true })
            {
                var probe = TunConnectivity.CheckControl(bound, referenceEngine.ProcessId, alive.Token);
                if (await Task.WhenAny(probe, runtime) == runtime) alive.Cancel();
                reference = reference with { After = await probe };
            }
            referenceEngine?.Dispose();
            referenceEngine = null;
            await Send(channel, new TunReply(true, "", observation, DiagnosticsPort: diagnosticsPort,
                EnginePid: engine.ProcessId, Control: control, Reference: reference), boot.Token);
            Task<string[]>? wfp = null;
            while (engine.Running)
            {
                if (await Task.WhenAny(runtime, Task.Delay(250)) != runtime) continue;
                if (!runtime.IsCompletedSuccessfully || runtime.Result <= 0) return 0;
                var events = diagnostic.TakeEvents(out var denials);
                if (denials.Length > 0)
                {
                    var evidence = new[] { "WFP · предыдущее чтение ещё выполняется." };
                    if (wfp == null || wfp.IsCompleted)
                    {
                        wfp = Task.Run(() =>
                        {
                            try { return TunWfpDiagnostic.Read(image, peer, profile.Port, source, denials); }
                            catch (Exception error) { return [$"WFP · чтение не завершено · код 0x{error.HResult:X8}."]; }
                        });
                        evidence = await Task.WhenAny(wfp, Task.Delay(750)) == wfp ? await wfp :
                            ["WFP · истекло время ожидания чтения."];
                    }
                    events = events.Concat(evidence).ToArray();
                }
                using var notice = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Send(channel, new TunReply(true, "", Sequence: runtime.Result, Events: events), notice.Token);
                runtime = Read<int>(channel, CancellationToken.None);
            }
            if (!runtime.IsCompleted && engine.ExitCode is { } exit)
            {
                diagnostic.Exited(exit);
                using var notice = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Send(channel, new TunReply(false, "", EngineNote: diagnostic.Description), notice.Token);
            }
            return 0;
        }
        catch (Exception error)
        {
            try
            {
                if (channel.IsConnected)
                {
                    var message = launchingTun && engine == null && error is VpnError ?
                        "Ядро Xray не завершило запуск Wintun. Подробности в отчёте." :
                        error is VpnError ? error.Message : "Windows отклонила TUN. Код 0x" + error.HResult.ToString("X8") + ".";
                    await Send(channel, new TunReply(false, message, observation, diagnostic.Description), boot.Token);
                }
            }
            catch { }
            return 1;
        }
        finally
        {
            if (engine != null)
            {
                try { if (console != null) engine.RequestGracefulStop(console.Signal); }
                finally { engine.Dispose(); }
            }
            referenceEngine?.Dispose();
            console?.Dispose();
            if (record != null && !boot.IsCancellationRequested)
            {
                // The normal path leaves no adapter. An abnormal exit is handled by the next explicit recovery.
                await TunWindows.WaitClean(record, TimeSpan.FromSeconds(2));
            }
        }
    }

    private static TunControlEndpoint PrivateControl(int port) => new(port, "pomogator",
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), TunConfig.InternetProbe, 443);

    private static async Task Send<T>(Stream socket, T message, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            if (data.Length > 8192) throw new IOException();
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
            await socket.WriteAsync(header, token);
            await socket.WriteAsync(data, token);
            await socket.FlushAsync(token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
    }

    private static async Task<T> Read<T>(Stream socket, CancellationToken token)
    {
        var header = new byte[4];
        await socket.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 8192) throw new IOException();
        var data = new byte[length];
        try
        {
            await socket.ReadExactlyAsync(data, token);
            return JsonSerializer.Deserialize<T>(data) ?? throw new IOException();
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int id);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int id);
}
