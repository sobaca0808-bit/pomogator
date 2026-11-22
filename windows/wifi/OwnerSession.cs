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
        public string WlanNote { get; set; }
    }

    internal sealed class OwnerSession
    {
        private readonly QueryHosted query;
        private readonly Func<long> interruptions;
        private readonly Action<string, string, bool> start;
        private readonly Func<HostedRelease> stop;
        private readonly Action<string, string> log;
        private readonly Func<int> interruptionReason;
        private readonly QueryHosted fresh;
        private readonly Action renew;
        private readonly Func<DateTime> clock;
        private Guid token, adapter;
        private string ssid, password;
        private long acquiredAt;
        private bool acquired, readFailed, stopping;
        private DateTime nextStart;
        private string lastStartFailure, interruptionNote;
        internal bool Retained => token != Guid.Empty;
        internal bool Stopping => stopping;

        internal OwnerSession(QueryHosted query, Func<long> interruptions, Func<int> interruptionReason,
            Action<string, string, bool> start, Func<HostedRelease> stop, Action<string, string> log,
            QueryHosted fresh, Action renew, Func<DateTime> clock = null)
        {
            this.query = query;
            this.interruptions = interruptions;
            this.interruptionReason = interruptionReason;
            this.start = start;
            this.stop = stop;
            this.log = log;
            this.fresh = fresh;
            this.renew = renew;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        internal OwnerReply Handle(OwnerRequest request)
        {
            switch (request.Command)
            {
                case "status":
                    return Observe();
                case "start":
                    if (Retained) throw new WifiError("У ПОМОГАТОРА уже есть сеанс точки доступа.");
                    PrepareStart();
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
                    if (current.Error != null) throw new WifiError(current.Error);
                    if (current.WindowsState != (int)HostedNetwork.State.Idle)
                        throw new WifiError("Восстановление доступно, когда Windows подтверждает, что точка выключена.");
                    PrepareStart();
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
            try { start(name, key, automatic); }
            catch (WlanFailure error)
            {
                nextStart = clock().AddSeconds(5);
                lastStartFailure = error.Message;
                throw;
            }
            lastStartFailure = null;
            interruptionNote = null;
            acquiredAt = before;
            acquired = true;
            adapter = Guid.Empty;
        }

        private void PrepareStart()
        {
            if (lastStartFailure != null && clock() < nextStart)
                throw new WifiError("Подождите несколько секунд перед повтором. " + lastStartFailure);
            // Confirm the state independently before closing a passive or obsolete local handle.
            if (fresh(out _) != HostedNetwork.State.Idle)
                throw new WifiError("Windows не подтвердила, что Hosted Network готова к запуску. Проверьте адаптер и другие точки доступа.");
            renew();
        }

        internal OwnerReply Observe()
        {
            try
            {
                var state = query(out var observed);
                if (readFailed) log("hosted_status_restored", "state=" + state);
                readFailed = false;

                if (acquired && interruptions() != acquiredAt)
                {
                    var reason = interruptionReason();
                    interruptionNote = HostedNetwork.Explain(0, (uint)reason);
                    Lost("windows_notification reason=" + reason + " " + interruptionNote);
                }
                if (acquired && state != HostedNetwork.State.Active)
                    Lost("windows_state=" + state);
                if (acquired && adapter != Guid.Empty && adapter != observed)
                    Lost("adapter_changed");
                if (acquired && observed != Guid.Empty && adapter == Guid.Empty)
                {
                    adapter = observed;
                    log("hosted_ownership_confirmed", "adapter=" + adapter);
                }

                return new OwnerReply { Owner = token, Adapter = observed, WindowsState = (int)state, WlanNote = interruptionNote,
                    Active = acquired && adapter != Guid.Empty && state == HostedNetwork.State.Active };
            }
            catch (Exception error)
            {
                if (!readFailed)
                    log("hosted_status_unconfirmed", error is WlanFailure ? error.Message : error.GetType().Name);
                readFailed = true;
                var note = (error as WlanFailure)?.Message ?? "Windows временно не подтверждает состояние точки Wi-Fi.";
                if (Retained && error is WlanFailure wlan && wlan.CanReadFresh)
                {
                    try
                    {
                        if (fresh(out var observed) == HostedNetwork.State.Idle)
                        {
                            if (acquired) Lost("fresh_wlan_state=Idle");
                            return new OwnerReply { Owner = token, Adapter = observed, WindowsState = (int)HostedNetwork.State.Idle,
                                WlanNote = "Точка выключена. Для восстановления будет открыт новый сеанс WLAN." };
                        }
                    }
                    catch (WlanFailure current) { note = current.Message; }
                }
                return new OwnerReply { Owner = token, Error = note };
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
