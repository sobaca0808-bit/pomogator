using System.Buffers.Binary;
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

internal sealed record TunRequest(string Peer, int SocksPort, string Password, bool Recover, TunBaseline Before);
internal sealed record TunReply(bool Success, string Message);

internal sealed class TunSession(TunRecovery record) : IDisposable
{
    private Process? helper;
    private NamedPipeServerStream? pipe;
    internal TunRecovery Record { get; } = record;
    internal bool Running => helper is { HasExited: false };

    private static string PipeName(Guid id) => "PomogatorTun-" + id.ToString("N");

    internal async Task StartAsync(int socksPort, string password, CancellationToken token)
    {
        if (TunWindows.OtherVpn()) throw new VpnError("Найден другой VPN или UBoost. Выберите локальный прокси либо завершите другой VPN самостоятельно.");
        if (File.Exists(LocalData.TunRecovery) || !TunWindows.Vacant(Record)) throw new VpnError("Имя или идентификатор интерфейса TUN уже занят.");
        LocalData.Save(LocalData.TunRecovery, Record);
        await Exchange(new TunRequest(Record.Peer, socksPort, password, false, Record.Before), token);
    }

    internal async Task RestoreAsync(CancellationToken token)
    {
        if (!TunWindows.Clean(Record))
        {
            await Exchange(new TunRequest(Record.Peer, 0, "", true, Record.Before), token);
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
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
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
            if (!reply.Success) throw new VpnError(reply.Message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new VpnError("Интерфейс TUN не подтвердил запуск за 35 секунд."); }
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
        using var boot = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        TunRecovery? record = null;
        Xray? engine = null;
        TunConsole? console = null;
        try
        {
            await channel.ConnectAsync(boot.Token);
            if (!GetNamedPipeServerProcessId(channel.SafePipeHandle, out var server) || server != parent) return 1;
            var request = await Read<TunRequest>(channel, boot.Token);
            if (!IPAddress.TryParse(request.Peer, out var peer) || peer.AddressFamily != AddressFamily.InterNetwork) return 1;
            record = new TunRecovery(id, peer.ToString(), request.Before);
            if (request.Recover)
            {
                var restored = await TunWindows.Recover(record);
                await Send(channel, new TunReply(restored, "Не удалось безопасно убрать оставшийся интерфейс TUN. Запись восстановления сохранена."), boot.Token);
                return restored ? 0 : 1;
            }

            if (request.SocksPort is < 1024 or > 65535 || request.Password.Length != 48 ||
                !request.Password.All(Uri.IsHexDigit)) return 1;
            if (!TunWindows.Vacant(record) || TunWindows.OtherVpn() ||
                !TunWindows.Same(request.Before, TunWindows.Capture(peer)))
                throw new VpnError("Перед запуском изменился рабочий интерфейс или появился другой VPN. Настройки не применены.");
            console = new TunConsole();
            var port = Xray.FreePort();
            var config = TunConfig.Engine(id, peer, request.SocksPort, request.Password, port);
            // Start watching the parent before the first network change, including driver initialization.
            using var alive = CancellationTokenSource.CreateLinkedTokenSource(boot.Token);
            var eof = channel.ReadAsync(new byte[1]).AsTask();
            var start = Xray.StartAsync(Path.Combine(AppContext.BaseDirectory, "xray.exe"), config, port,
                alive.Token, WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback, inheritConsole: true, startupSeconds: 24);
            if (await Task.WhenAny(start, eof) == eof) alive.Cancel();
            engine = await start;
            var observation = TunWindows.Observe(record);
            if (!observation.Interface || !observation.Routes || !observation.Dns)
                throw new VpnError("Windows не подтвердила интерфейс, маршруты или DNS туннеля.");
            await Send(channel, new TunReply(true, ""), boot.Token);
            while (engine.Running && !eof.IsCompleted) await Task.WhenAny(eof, Task.Delay(500));
            return 0;
        }
        catch (Exception error)
        {
            try
            {
                if (channel.IsConnected)
                    await Send(channel, new TunReply(false, error is VpnError ? error.Message :
                        "Windows отклонила TUN. Код 0x" + error.HResult.ToString("X8") + "."), boot.Token);
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
            console?.Dispose();
            if (record != null && !boot.IsCancellationRequested)
            {
                // The normal path leaves no adapter. An abnormal exit is handled by the next explicit recovery.
                await TunWindows.WaitClean(record, TimeSpan.FromSeconds(2));
            }
        }
    }

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
