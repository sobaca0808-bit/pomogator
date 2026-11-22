using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PomogatorWifi
{
    public sealed class SharingCommand
    {
        public string Action { get; set; }
        public SharingLease Pair { get; set; }
    }

    public sealed class SharingReply
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    internal static class IcsWorker
    {
        private static string Name(Guid id) => "PomogatorWifi.Ics." + id.ToString("N");

        internal static async Task<SharingReply> Run(SharingCommand command)
        {
            if (Wire.IsAdministrator()) return await NetworkReading.OnSta(() => Perform(command));
            var id = Guid.NewGuid();
            using (var server = Wire.Server(Name(id)))
            {
                Process helper;
                try
                {
                    helper = await Task.Run(() => Process.Start(new ProcessStartInfo(Wire.Executable,
                        "--wifi-ics " + id.ToString("N") + " " + Process.GetCurrentProcess().Id)
                        { UseShellExecute = true, Verb = "runas" }));
                }
                catch (Win32Exception error) when (error.NativeErrorCode == 1223)
                {
                    return new SharingReply { Message = "Повышение прав для изменения ICS отменено." };
                }
                if (helper == null) return new SharingReply { Message = "Помощник ICS не запустился." };
                using (helper)
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                {
                    try
                    {
                        await server.WaitForConnectionAsync(deadline.Token);
                        if (!Wire.ValidatePeer(server, false, helper.Id)) throw new IOException();
                        await Wire.Write(server, command, deadline.Token);
                        return await Wire.Read<SharingReply>(server, deadline.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Local.Log("ics_helper_timeout");
                        return new SharingReply { Message = "Нет ответа помощника ICS. Обновите диагностику перед повтором." };
                    }
                }
            }
        }

        internal static int Serve(Guid pipeId, int parent)
        {
            if (!Wire.IsAdministrator()) return 1;
            using (var pipe = new NamedPipeClientStream(".", Name(pipeId), PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(24)))
            {
                try
                {
                    pipe.Connect(5000);
                    if (!Wire.ValidatePeer(pipe, true, parent)) return 1;
                    var command = Wire.Read<SharingCommand>(pipe, deadline.Token).GetAwaiter().GetResult();
                    Wire.Write(pipe, Perform(command), deadline.Token).GetAwaiter().GetResult();
                    return 0;
                }
                catch (Exception error) { Local.LogError("ics_helper_failed", error); return 1; }
            }
        }

        private static SharingReply Perform(SharingCommand command)
        {
            using (var mutex = new Mutex(false, @"Local\PomogatorWifi.IcsChange." + Wire.UserSession))
            {
                bool held;
                try { held = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return new SharingReply { Message = "Другая операция ICS ещё выполняется." };
                try { return Mutate(command); }
                catch (Exception error)
                {
                    Local.LogError("ics_write_failed", error);
                    if (error is WifiError) Local.Log("ics_detail", error.Message);
                    return new SharingReply { Message = "Общий доступ не изменён либо не подтверждён. Подробности в диагностике." };
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static SharingReply Mutate(SharingCommand command)
        {
            var pair = command.Pair;
            if (pair == null || pair.Owner == Guid.Empty || pair.Source == Guid.Empty || pair.Target == Guid.Empty || pair.Source == pair.Target)
                throw new WifiError("Некорректная пара ICS.");
            var owner = Owner.Request(new OwnerRequest { Command = "status" });
            if (owner == null || owner.Owner != pair.Owner || owner.Adapter != pair.Target || !owner.Active)
                throw new WifiError("Точка доступа больше не принадлежит этому сеансу.");

            using (var wlan = new HostedNetwork())
            {
                var lease = Local.Load<SharingLease>(Local.LeasePath);
                var matches = lease != null && lease.Owner == pair.Owner && lease.Source == pair.Source && lease.Target == pair.Target;
                if (command.Action == "restore" || command.Action == "repair")
                {
                    if (!matches) throw new WifiError("У этой пары нет подтверждённого разрешения.");
                }
                else if (command.Action != "enable" || lease?.Owner == pair.Owner)
                    throw new WifiError("Существует незавершённая настройка ICS.");

                Func<IcsSnapshot> open = NetworkReading.OpenIcs;
                var session = Session.ForPair(wlan.Query, open, pair.Source, pair.Target);
                if (command.Action == "restore")
                {
                    session.RestoreOwnedPair();
                    using (var snapshot = open())
                    {
                        snapshot.RequireVerified(pair.Source, pair.Target);
                        if (snapshot.Connections.Exists(connection => (connection.Id == pair.Source || connection.Id == pair.Target) &&
                            connection.Sharing.HasValue)) throw new WifiError("Windows не подтвердила возврат настроек ICS.");
                    }
                    File.Delete(Local.LeasePath);
                    Local.Log("ics_restored");
                    return new SharingReply { Success = true };
                }

                var repair = command.Action == "repair";
                try
                {
                    var result = session.ConfigureSharing(_ => true, repair ? (Action)null : () => Local.Save(Local.LeasePath, pair), repair);
                    Local.Log(result == SharingResult.Existing ? "ics_existing_pair" : "ics_verified");
                    return new SharingReply { Success = true };
                }
                catch
                {
                    // On initial enable, only revert our own attempted edits. A repair preserves surviving roles.
                    if (!repair && session.OwnsChanges)
                    {
                        try { session.Restore(); File.Delete(Local.LeasePath); }
                        catch (Exception cleanup) { Local.LogError("ics_rollback_failed", cleanup); }
                    }
                    throw;
                }
            }
        }
    }
}
