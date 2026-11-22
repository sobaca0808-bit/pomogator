using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PomogatorWifi
{
    internal static class Wire
    {
        internal static string Executable => Assembly.GetExecutingAssembly().Location;
        internal static string UserSession
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                using (var process = Process.GetCurrentProcess())
                    return id.User.Value + "." + process.SessionId;
            }
        }

        internal static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal static byte[] Encode<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        internal static T Decode<T>(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        internal static async Task Write<T>(Stream stream, T value, CancellationToken token)
        {
            var bytes = Encode(value);
            try
            {
                if (bytes.Length > 8192) throw new IOException();
                var header = BitConverter.GetBytes(bytes.Length);
                await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        internal static async Task<T> Read<T>(Stream stream, CancellationToken token)
        {
            var header = new byte[4];
            await Fill(stream, header, token).ConfigureAwait(false);
            var length = BitConverter.ToInt32(header, 0);
            if (length < 1 || length > 8192) throw new IOException();
            var bytes = new byte[length];
            try
            {
                await Fill(stream, bytes, token).ConfigureAwait(false);
                return Decode<T>(bytes);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private static async Task Fill(Stream stream, byte[] data, CancellationToken token)
        {
            for (var offset = 0; offset < data.Length;)
            {
                var count = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
        }

        internal static NamedPipeServerStream Server(string name)
        {
            var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
            {
                security.SetOwner(identity.User);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 8192, 8192, security);
        }

        internal static bool ValidatePeer(PipeStream pipe, bool server, int expectedPid = 0)
        {
            int pid;
            var ok = server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid)
                : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid);
            if (!ok || pid <= 0 || expectedPid != 0 && pid != expectedPid) return false;
            using (var process = OpenProcess(0x1000, false, pid))
            {
                if (process.IsInvalid) return false;
                var path = new StringBuilder(32768);
                var size = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref size) &&
                    string.Equals(path.ToString(), Executable, StringComparison.OrdinalIgnoreCase);
            }
        }

        [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int id);
        [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int id);
        [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(
            SafeProcessHandle process, int flags, StringBuilder path, ref int length);
    }

    internal static class Owner
    {
        private static string Name => "PomogatorWifi.Owner." + Wire.UserSession;
        private static string GuardName => @"Local\PomogatorWifi.Owner." + Wire.UserSession;

        internal static bool Exists()
        {
            if (!Mutex.TryOpenExisting(GuardName, out var guard)) return false;
            guard.Dispose();
            return true;
        }

        internal static OwnerReply Request(OwnerRequest request, int connectMs = 700) =>
            Exchange(Name, request, connectMs, pipe => Wire.ValidatePeer(pipe, true));

        internal static OwnerReply Exchange(string name, OwnerRequest request, int connectMs, Func<PipeStream, bool> verify)
        {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try { pipe.Connect(connectMs); }
                catch (TimeoutException) { return null; }
                if (!verify(pipe)) throw new WifiError("Сеанс принадлежит другой копии программы.");
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    Wire.Write(pipe, request, deadline.Token).GetAwaiter().GetResult();
                    return Wire.Read<OwnerReply>(pipe, deadline.Token).GetAwaiter().GetResult();
                }
            }
        }

        internal static OwnerReply Start(string ssid, string password, bool automatic, string origin)
        {
            if (!Exists())
                using (Process.Start(new ProcessStartInfo(Wire.Executable, "--wifi-owner") { UseShellExecute = false, CreateNoWindow = true })) { }
            return Request(new OwnerRequest { Command = "start", Ssid = ssid, Password = password, Automatic = automatic, Origin = origin }, 3500)
                ?? throw new WifiError("Не удалось связаться с помощником точки доступа.");
        }

        internal static int Serve()
        {
            using (var guard = new Mutex(true, GuardName, out var first))
            {
                if (!first) return 0;
                using (var wlan = new HostedNetwork(monitor: true))
                {
                    var session = new OwnerSession(wlan.Query, () => wlan.Interruptions, () => wlan.InterruptionReason,
                        (ssid, password, automatic) => StartChecked(wlan, ssid, password, automatic), wlan.Stop, Local.Log);
                    try { ServeTransport(Name, session, pipe => Wire.ValidatePeer(pipe, false), Local.Log); }
                    finally
                    {
                        if (session.Retained) Local.Log("hosted_handle_closing", "source=helper_exit");
                    }
                }
            }
            return 0;
        }

        internal static void ServeTransport(string name, OwnerSession session, Func<PipeStream, bool> verify,
            Action<string, string> log)
        {
            var idle = Stopwatch.StartNew();
            // Keep the server instance available across polls and GUI restarts. A pipe fault never expires an owned session.
            while (!session.Stopping && (session.Retained || idle.Elapsed < TimeSpan.FromSeconds(15)))
            {
                try
                {
                    using (var pipe = Wire.Server(name))
                        while (!session.Stopping && (session.Retained || idle.Elapsed < TimeSpan.FromSeconds(15)))
                        {
                            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                            {
                                try { pipe.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult(); }
                                catch (OperationCanceledException)
                                {
                                    if (session.Retained) session.Observe();
                                    continue;
                                }
                            }
                            try
                            {
                                if (!verify(pipe)) continue;
                                using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                                {
                                    OwnerReply reply;
                                    try
                                    {
                                        var request = Wire.Read<OwnerRequest>(pipe, limit.Token).GetAwaiter().GetResult();
                                        reply = session.Handle(request);
                                    }
                                    catch (Exception error)
                                    {
                                        log("wlan_operation_failed", error is WifiError ? error.Message : error.GetType().Name);
                                        reply = session.Observe();
                                        reply.Error = error is WifiError && !(error is WlanFailure) ? error.Message :
                                            "Windows не завершила операцию Wi-Fi. Подробности в диагностике.";
                                    }
                                    Wire.Write(pipe, reply, limit.Token).GetAwaiter().GetResult();
                                }
                                // DisconnectNamedPipe discards unread bytes. The client closes after reading the reply.
                                using (var close = new CancellationTokenSource(TimeSpan.FromMilliseconds(750)))
                                {
                                    var end = new byte[1];
                                    try { pipe.ReadAsync(end, 0, end.Length, close.Token).GetAwaiter().GetResult(); }
                                    catch (OperationCanceledException) { }
                                }
                            }
                            catch (Exception error) when (error is IOException || error is OperationCanceledException)
                            { log("hosted_pipe_interrupted", error.GetType().Name); }
                            finally { pipe.Disconnect(); }
                        }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                {
                    log("hosted_pipe_unavailable", error.GetType().Name);
                    if (session.Retained) session.Observe();
                    Thread.Sleep(250);
                }
            }
        }

        private static void StartChecked(HostedNetwork wlan, string ssid, string password, bool automatic)
        {
            var mobile = MobileHotspot.Read();
            if (mobile == MobilePhase.On || mobile == MobilePhase.Transition)
                throw new WifiError("Мобильный хот-спот Windows уже работает.");
            using (var snapshot = NetworkReading.TryIcs())
            {
                var direct = snapshot != null && (NetworkReading.WifiDirect(snapshot) ||
                    snapshot.Connections.Exists(connection => connection.Connected &&
                        (connection.Flags & Characteristics.WifiDirect) != 0) ||
                    snapshot.Issues.Exists(issue => issue.ActiveWifiDirect));
                if (direct)
                    throw new WifiError("Обнаружена действующая точка доступа Wi-Fi Direct.");
                if (automatic && mobile == MobilePhase.Unknown &&
                    (snapshot == null || snapshot.Issues.Exists(issue => !issue.Unsupported) ||
                     snapshot.Connections.Exists(connection => connection.Sharing.HasValue || connection.Connected &&
                         (connection.Flags & Characteristics.WifiDirect) != 0)))
                    throw new WifiError("Автозапуск остановлен: не удалось надёжно исключить действующую точку доступа.");
            }
            if (ssid == null || password == null) throw new WifiError("Сохраните параметры сети.");
            var name = Policy.EncodeSsid(ssid);
            Policy.CheckPassword(password);
            wlan.Start(name, password);
        }
    }
}
