using System;

namespace PomogatorWifi
{
    public sealed class OwnerRequest
    {
        public string Command { get; set; }
        public Guid Owner { get; set; }
        public Guid Adapter { get; set; }
        public string Origin { get; set; }
        public string Ssid { get; set; }
        public string Password { get; set; }
        public bool Automatic { get; set; }
    }

    public sealed class OwnerReply
    {
        public Guid Owner { get; set; }
        public Guid Adapter { get; set; }
        public int? WindowsState { get; set; }
        public bool Active { get; set; }
        public string Error { get; set; }
    }

    internal sealed class OwnerSession
    {
        private readonly QueryHosted query;
        private readonly Func<long> interruptions;
        private readonly Action<string, string, bool> start;
        private readonly Func<HostedRelease> stop;
        private readonly Action<string, string> log;
        private readonly Func<int> interruptionReason;
        private Guid token, adapter;
        private string ssid, password;
        private long acquiredAt;
        private bool acquired, readFailed, stopping;
        internal bool Retained => token != Guid.Empty;
        internal bool Stopping => stopping;

        internal OwnerSession(QueryHosted query, Func<long> interruptions, Func<int> interruptionReason,
            Action<string, string, bool> start, Func<HostedRelease> stop, Action<string, string> log)
        {
            this.query = query;
            this.interruptions = interruptions;
            this.interruptionReason = interruptionReason;
            this.start = start;
            this.stop = stop;
            this.log = log;
        }

        internal OwnerReply Handle(OwnerRequest request)
        {
            switch (request.Command)
            {
                case "status":
                    return Observe();
                case "start":
                    if (Retained) throw new WifiError("У ПОМОГАТОРА уже есть сеанс точки доступа.");
                    Acquire(request.Ssid, request.Password, request.Automatic);
                    ssid = request.Ssid;
                    password = request.Password;
                    token = Guid.NewGuid();
                    log("hosted_started", request.Automatic ? "source=automation" : "source=" + Origin(request));
                    return Observe();
                case "recover":
                    RequireSession(request);
                    var current = Observe();
                    if (current.Active) return current;
                    if (current.WindowsState != (int)HostedNetwork.State.Idle)
                        throw new WifiError("Восстановление доступно, когда Windows подтверждает, что точка выключена.");
                    stop(); // Release any reference left by Windows on this same client handle.
                    Acquire(ssid, password, request.Automatic);
                    log("hosted_recovered", "source=" + Origin(request));
                    return Observe();
                case "stop":
                    RequireSession(request);
                    if (request.Origin != "window" && request.Origin != "tray")
                        throw new WifiError("Команда выключения не подтверждена.");
                    current = Observe();
                    if (!current.Active || request.Adapter == Guid.Empty || request.Adapter != current.Adapter)
                        throw new WifiError("Состояние точки изменилось. Обновите его перед выключением.");
                    var release = stop();
                    token = adapter = Guid.Empty;
                    password = ssid = null;
                    acquired = false;
                    stopping = true;
                    current = Observe();
                    log("hosted_released", $"source={Origin(request)} win32={release.Code} reason={release.Reason} " +
                        $"state={current.WindowsState?.ToString() ?? "unknown"}");
                    // The requested reference was released even if a later state query fails.
                    current.Error = null;
                    return current;
                default:
                    throw new WifiError("Неизвестная операция.");
            }
        }

        private void RequireSession(OwnerRequest request)
        {
            if (!Retained || request.Owner != token) throw new WifiError("Сеанс точки доступа изменился.");
        }

        private void Acquire(string name, string key, bool automatic)
        {
            // A drop observed after this point invalidates the reference even if the same GUID reappears.
            var before = interruptions();
            start(name, key, automatic);
            acquiredAt = before;
            acquired = true;
            adapter = Guid.Empty;
        }

        internal OwnerReply Observe()
        {
            try
            {
                var state = query(out var observed);
                if (readFailed) log("hosted_status_restored", "state=" + state);
                readFailed = false;

                if (acquired && interruptions() != acquiredAt)
                    Lost("windows_notification reason=" + interruptionReason());
                if (acquired && state != HostedNetwork.State.Active)
                    Lost("windows_state=" + state);
                if (acquired && adapter != Guid.Empty && adapter != observed)
                    Lost("adapter_changed");
                if (acquired && observed != Guid.Empty && adapter == Guid.Empty)
                {
                    adapter = observed;
                    log("hosted_ownership_confirmed", "adapter=" + adapter);
                }

                return new OwnerReply { Owner = token, Adapter = observed, WindowsState = (int)state,
                    Active = acquired && adapter != Guid.Empty && state == HostedNetwork.State.Active };
            }
            catch (Exception error)
            {
                if (!readFailed)
                    log("hosted_status_unconfirmed", error is WlanFailure ? error.Message : error.GetType().Name);
                readFailed = true;
                return new OwnerReply { Owner = token, Error = "Windows временно не подтверждает состояние точки Wi-Fi." };
            }
        }

        private void Lost(string reason)
        {
            acquired = false;
            log("hosted_ownership_lost", reason);
        }

        private static string Origin(OwnerRequest request)
        {
            switch (request.Origin)
            {
                case "window":
                case "tray":
                case "automation": return request.Origin;
                default: return "unknown";
            }
        }
    }

    internal readonly struct HostedRelease
    {
        internal readonly uint Code, Reason;
        internal HostedRelease(uint code, uint reason) { Code = code; Reason = reason; }
    }
}
