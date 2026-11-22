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

    public sealed class OwnerRequest
    {
        public string Command { get; set; }
        public Guid Owner { get; set; }
        public string Ssid { get; set; }
        public string Password { get; set; }
        public bool Automatic { get; set; }
    }

    public sealed class OwnerReply
    {
        public Guid Owner { get; set; }
        public Guid Adapter { get; set; }
        public bool Active { get; set; }
        public string Error { get; set; }
    }

    internal static class Owner
    {
        private static string Name => "PomogatorWifi.Owner." + Wire.UserSession;

        internal static OwnerReply Request(OwnerRequest request, int connectMs = 450)
        {
            using (var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try { pipe.Connect(connectMs); }
                catch (TimeoutException) { return null; }
                if (!Wire.ValidatePeer(pipe, true)) throw new WifiError("Другой экземпляр программы держит сеанс. Откройте ту же копию утилиты.");
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                {
                    Wire.Write(pipe, request, deadline.Token).GetAwaiter().GetResult();
                    return Wire.Read<OwnerReply>(pipe, deadline.Token).GetAwaiter().GetResult();
                }
            }
        }

        internal static OwnerReply Start(string ssid, string password, bool automatic)
        {
            if (Request(new OwnerRequest { Command = "status" }, 120) == null)
                using (Process.Start(new ProcessStartInfo(Wire.Executable, "--wifi-owner") { UseShellExecute = false, CreateNoWindow = true })) { }
            return Request(new OwnerRequest { Command = "start", Ssid = ssid, Password = password, Automatic = automatic }, 3500)
                ?? throw new WifiError("Не удалось связаться с помощником точки доступа.");
        }

        internal static int Serve()
        {
            using (var guard = new Mutex(true, @"Local\PomogatorWifi.Owner." + Wire.UserSession, out var first))
            {
                if (!first) return 0;
                using (var wlan = new HostedNetwork())
                {
                    var token = Guid.Empty;
                    string ssid = null, password = null;
                    var idle = Stopwatch.StartNew();
                    while (token != Guid.Empty || idle.Elapsed < TimeSpan.FromSeconds(15))
                    {
                        using (var pipe = Wire.Server(Name))
                        using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        {
                            try { pipe.WaitForConnectionAsync(connect.Token).GetAwaiter().GetResult(); }
                            catch (Exception error) when (error is OperationCanceledException || error is IOException) { continue; }
                            if (!Wire.ValidatePeer(pipe, false)) continue;
                            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                            {
                                OwnerReply reply;
                                var command = "";
                                try
                                {
                                    var request = Wire.Read<OwnerRequest>(pipe, deadline.Token).GetAwaiter().GetResult();
                                    command = request.Command;
                                    if (command == "start")
                                    {
                                        if (token != Guid.Empty) throw new WifiError("У ПОМОГАТОРА уже есть сеанс точки доступа.");
                                        StartChecked(wlan, request.Ssid, request.Password, request.Automatic);
                                        ssid = request.Ssid;
                                        password = request.Password;
                                        token = Guid.NewGuid();
                                        Local.Log("hosted_started");
                                    }
                                    else if (command == "stop" || command == "recover")
                                    {
                                        if (request.Owner == Guid.Empty || request.Owner != token)
                                            throw new WifiError("Сеанс точки доступа изменился.");
                                        if (command == "stop")
                                        {
                                            wlan.Stop();
                                            token = Guid.Empty;
                                            password = null;
                                            idle.Restart();
                                            Local.Log("hosted_released");
                                        }
                                        else if (wlan.Query(out _) == HostedNetwork.State.Idle)
                                        {
                                            wlan.Stop();
                                            StartChecked(wlan, ssid, password, true);
                                            Local.Log("hosted_recovered");
                                        }
                                    }
                                    else if (command != "status") throw new WifiError("Неизвестная операция.");
                                    var state = wlan.Query(out var adapter);
                                    reply = new OwnerReply { Owner = token, Adapter = adapter,
                                        Active = token != Guid.Empty && wlan.Started && state == HostedNetwork.State.Active };
                                }
                                catch (Exception error)
                                {
                                    Local.LogError("wlan_operation_failed", error);
                                    if (error is WifiError) Local.Log("wlan_detail", error.Message);
                                    reply = new OwnerReply { Error = error is WifiError && !error.Message.Contains("(WLAN ") ? error.Message : "Windows не завершила операцию Wi-Fi. Подробности в диагностике." };
                                }
                                try { Wire.Write(pipe, reply, deadline.Token).GetAwaiter().GetResult(); }
                                catch (Exception error) when (error is IOException || error is OperationCanceledException) { }
                                if (command == "stop" && token == Guid.Empty) return 0;
                            }
                        }
                    }
                }
            }
            return 0;
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
