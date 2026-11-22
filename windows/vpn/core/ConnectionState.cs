namespace Pomogator.Vpn;

public enum Phase { Disconnected, Connecting, Connected, Error, Disconnecting }
public enum Check { Xray, Proxy, Https }
public enum CheckResult { Pending, Passed, Failed }

public sealed class ConnectionState
{
    private readonly CheckResult[] checks = new CheckResult[3];
    public event Action? Changed;
    public Phase Phase { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public bool SystemProxy { get; private set; }
    public CheckResult Result(Check check) => checks[(int)check];

    public void Begin()
    {
        if (Phase is not (Phase.Disconnected or Phase.Error)) throw new InvalidOperationException();
        Array.Clear(checks);
        Change(Phase.Connecting);
    }

    public void Record(Check check, CheckResult result)
    {
        if (Phase is not (Phase.Connecting or Phase.Connected)) return;
        checks[(int)check] = result;
        Changed?.Invoke();
    }

    public void Confirm(DateTimeOffset now, bool systemProxy)
    {
        if (Phase != Phase.Connecting || checks.Any(result => result != CheckResult.Passed))
            throw new InvalidOperationException("All checks must pass before confirmation.");
        ConnectedAt = now;
        SystemProxy = systemProxy;
        Change(Phase.Connected);
    }

    public void ReleaseSystemProxy()
    {
        SystemProxy = false;
        Changed?.Invoke();
    }

    public void BeginStop()
    {
        if (Phase is Phase.Connecting or Phase.Connected or Phase.Error) Change(Phase.Disconnecting);
    }

    public void End(bool failed)
    {
        ConnectedAt = null;
        SystemProxy = false;
        if (!failed) Array.Clear(checks);
        Change(failed ? Phase.Error : Phase.Disconnected);
    }

    private void Change(Phase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }
}
