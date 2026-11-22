using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PomogatorWifi
{
    public sealed class HostedStopReply
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    internal static class HostedStopWorker
    {
        private static string PipeName(Guid id) => "PomogatorWifi.HostedStop." + id.ToString("N");

        internal static async Task Run(HostedNetwork watcher, HostedTarget approved, long generation)
        {
            if (Wire.IsAdministrator())
            {
                await Task.Run(() => Stop(watcher, approved, generation));
                return;
            }

            var id = Guid.NewGuid();
            using (var pipe = Wire.Server(PipeName(id)))
            {
                Process helper;
                try
                {
                    var parent = Process.GetCurrentProcess().Id;
                    helper = await Task.Run(() => Process.Start(new ProcessStartInfo(Wire.Executable,
                        $"--wifi-stop {id:N} {parent}") { UseShellExecute = true, Verb = "runas" }));
                }
                catch (Win32Exception error) when (error.NativeErrorCode == 1223)
                {
                    throw new WifiError("Отключение отменено в запросе прав администратора.");
                }
                if (helper == null) throw new WifiError("Помощник отключения не запустился.");
                using (helper)
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(deadline.Token);
                        if (!Wire.ValidatePeer(pipe, false, helper.Id))
                            throw new WifiError("Не удалось подтвердить помощника отключения.");
                        var ready = await Wire.Read<HostedStopReply>(pipe, deadline.Token);
                        RequireSuccess(ready);
                        HostedStop.RequireMatch(approved, generation, watcher.ActiveTarget(), watcher.Interruptions);
                        await Wire.Write(pipe, approved, deadline.Token);
                        RequireSuccess(await Wire.Read<HostedStopReply>(pipe, deadline.Token));
                    }
                    catch (OperationCanceledException)
                    { throw new WifiError("Помощник не ответил вовремя. Проверьте текущее состояние раздачи."); }
                    catch (IOException)
                    { throw new WifiError("Связь с помощником прервалась. Проверьте текущее состояние раздачи."); }
                }
            }
        }

        internal static int Serve(Guid id, int parent)
        {
            if (!Wire.IsAdministrator()) return 1;
            using (var pipe = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                try
                {
                    pipe.Connect(5000);
                    if (!Wire.ValidatePeer(pipe, true, parent)) return 1;
                    using (var watcher = new HostedNetwork(monitor: true))
                    {
                        var generation = watcher.Interruptions;
                        var target = watcher.ActiveTarget();
                        if (target == null) throw new WifiError("Windows не подтвердила активную точку Hosted Network.");
                        SendReply(new HostedStopReply { Success = true });
                        var approved = Wire.Read<HostedTarget>(pipe, deadline.Token).GetAwaiter().GetResult();
                        if (!target.Matches(approved)) throw new WifiError("Обнаружена другая точка. Отключение отменено.");
                        Stop(watcher, approved, generation);
                        SendReply(new HostedStopReply { Success = true });
                        return 0;
                    }
                }
                catch (Exception error)
                {
                    Local.LogError("hosted_manual_stop_failed", error);
                    if (pipe.IsConnected)
                        try { SendReply(new HostedStopReply { Error = error is WifiError ? error.Message : "Windows не завершила отключение точки." }); }
                        catch (Exception send) when (send is IOException || send is OperationCanceledException) { }
                    return 1;
                }

                void SendReply(HostedStopReply reply) => Wire.Write(pipe, reply, deadline.Token).GetAwaiter().GetResult();
            }
        }

        private static void Stop(HostedNetwork watcher, HostedTarget approved, long generation)
        {
            HostedStop.Apply(approved, generation, watcher.ActiveTarget, () => watcher.Interruptions, watcher.ForceStop);
            if (!HostedStop.WaitForOff(() => watcher.Query(out _), () => Thread.Sleep(200)))
                throw new WifiError("Windows по-прежнему сообщает об активной Hosted Network. Возможно, другая программа запустила её снова.");
            Local.Log("hosted_manual_stop_confirmed");
        }

        private static void RequireSuccess(HostedStopReply reply)
        {
            if (!reply.Success) throw new WifiError(reply.Error ?? "Помощник не подтвердил отключение.");
        }
    }
}
