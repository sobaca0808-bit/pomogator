using System.Reflection;
using System.Runtime.InteropServices;
using PomogatorWifiLite;

var passed = 0;
var target = Guid.NewGuid();
var source = Guid.NewGuid();
const string key = "fixture12345";

Test("Start waits for the native adapter and reads counts of one and three clients", () => {
    var radio = new FakeRadio(target) { Delay = 2 };
    using var session = radio.Session();
    session.Start("Проверка", key);
    radio.Peers = 1;
    Check(session.Read() is { Ours: true, State: PointState.Active, Clients: 1 });
    radio.Peers = 3;
    Check(session.Read().Clients == 3 && radio.Starts == 1);
    session.Stop();
    Check(!session.CanStop && radio.Stops == 1 && session.Read().State == PointState.Idle);
    session.Start("Проверка", key);
    Check(radio.Starts == 2 && session.Read().Ours);
});
Test("Reject a concurrent manager and leave an external access point running", () => {
    var radio = new FakeRadio(target) { Previous = true };
    using var session = radio.Session();
    Reject(() => session.Start("test", key));
    radio.Previous = false;
    radio.State = PointState.Active;
    Reject(() => session.Start("test", key));
    session.Stop();
    Check(session.Read() is { Ours: false, State: PointState.Active } && radio.Starts == 0 && radio.Stops == 0);
});
Test("An interruption or a late competing app releases only the Lite reference", () => {
    var radio = new FakeRadio(target);
    using var session = radio.Session();
    session.Start("test", key);
    radio.Interrupted = true;
    radio.External = true;
    Check(session.Read() is { Ours: false, State: PointState.Active } && !session.CanStop);
    Check(radio.Releases == 1 && radio.Stops == 0);
    radio.External = false;
    radio.State = PointState.Idle;
    radio.Interrupted = false;
    session.Start("test", key);
    radio.Previous = true;
    Check(session.Read().OtherController && radio.Releases == 2 && radio.Stops == 0);
});
Test("Reject a start that Windows never confirms and clean up its handle", () => {
    var radio = new FakeRadio(target) { Delay = 30 };
    using var session = radio.Session();
    Reject(() => session.Start("test", key));
    Check(!session.CanStop && radio.Releases == 1 && radio.Starts == 1);
});
Test("Keep native error codes visible for launch and stop failures", () => {
    var radio = new FakeRadio(target) { LaunchError = true };
    using var session = radio.Session();
    Check(Reject(() => session.Start("test", key)).Contains("WlanHostedNetworkStartUsing · Win32 31 · причина WLAN 19"));
    Check(!session.CanStop);
    radio.LaunchError = false;
    session.Start("test", key);
    radio.StopError = true;
    Check(Reject(session.Stop).Contains("Win32 1062") && !session.CanStop && radio.Releases == 1);
});
Test("Validate SSID UTF-8 length and WPA2 ASCII before touching WLAN", () => {
    var radio = new FakeRadio(target);
    using var session = radio.Session();
    Reject(() => session.Start(new string('я', 17), key));
    Reject(() => session.Start("test\n", key));
    Reject(() => session.Start("test", "кириллица-123"));
    Reject(() => session.Start("test", "1234567"));
    Check(radio.Opens == 0);
});
Test("Verify exact ICS roles and keep phone Internet as a separate check", () => {
    var known = new HashSet<Guid>();
    var wan = new IcsConnection { Id = source, Media = 6, Status = 2, Name = "PPPoE fixture" };
    var wifi = new IcsConnection { Id = target, Media = 3, Status = 2, Name = "Hosted fixture" };
    var list = new List<IcsConnection> { wan, wifi };
    Check(IcsProbe.Summarize(list, Guid.Empty, known).Contains("после запуска"));
    Check(IcsProbe.Summarize(list, target, known).Contains("ICS выключен"));
    wan.Media = 9;
    wan.Role = 0;
    wifi.Role = 1;
    Check(IcsProbe.Summarize(list, target, known).Contains("Роли ICS подтверждены"));
    Check(IcsProbe.Summarize(list, target, known).Contains("Интернет проверьте на телефоне"));
    Check(IcsProbe.Summarize(list, target, []).Contains("Тип PPPoE"));
    Check(!IcsProbe.Summarize(list, Guid.NewGuid(), known).Contains("Роли ICS подтверждены"));
    list.Add(new IcsConnection { Id = Guid.NewGuid(), Role = 1, Status = 2 });
    Check(IcsProbe.Summarize(list, target, known).Contains("другой пары"));
    list.RemoveAt(2);
    list.Add(new IcsConnection { Id = Guid.NewGuid(), Media = 6, Status = 2 });
    Check(IcsProbe.Summarize(list, target, known).Contains("несколько PPPoE"));
});
Test("Match Windows SDK WLAN and read-only COM ABI", () => {
    var status = typeof(Wlan).GetNestedType("Status", BindingFlags.NonPublic)!;
    Check(Marshal.SizeOf(status) == 40 && Marshal.OffsetOf(status, "Peers").ToInt32() == 36);
    Check(typeof(IShareManager).GUID == Guid.Parse("C08956B7-1CD3-11D1-B1C5-00805FC1270E"));
    var method = typeof(IShareManager).GetMethod("Configuration")!;
    Check(method.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig) && method.GetParameters()[1].IsOut);
    Check(typeof(IShareConfiguration).GetMethods().Select(item => item.Name).SequenceEqual(new[] { "Enabled", "Role" }));
});
Test("Count only peers authenticated by Windows", () => {
    var peers = Marshal.AllocHGlobal(48);
    try
    {
        for (var index = 0; index < 48; index++) Marshal.WriteByte(peers, index, 0);
        for (var index = 0; index < 3; index++) Marshal.WriteInt32(peers, index * 12 + 8, 1);
        Check(Wlan.CountAuthenticated(peers, 4) == 3 && Wlan.CountAuthenticated(IntPtr.Zero, 0) == 0);
    }
    finally { Marshal.FreeHGlobal(peers); }
});

Console.WriteLine($"{passed} checks passed.");

void Test(string description, Action assertion)
{
    assertion();
    passed++;
    Console.WriteLine("PASS " + description);
}
static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Unexpected result.");
}
static string Reject(Action action)
{
    try { action(); }
    catch (WifiException error) { return error.Message; }
    throw new InvalidOperationException("An expected refusal was not raised.");
}

sealed class FakeRadio(Guid adapter)
{
    internal PointState State = PointState.Idle;
    internal bool Previous, Interrupted, External, LaunchError, StopError;
    internal int Starts, Stops, Releases, Delay, Opens;
    internal uint Peers;
    internal HostedSession Session() => new(() => { Opens++; return new Client(this, adapter); }, () => Previous, () => { });

    private sealed class Client(FakeRadio radio, Guid adapter) : IHostedNetwork
    {
        private bool owned;
        public bool Interrupted => radio.Interrupted;
        public PointStatus Read()
        {
            if (owned && radio.Delay > 0 && --radio.Delay == 0) radio.State = PointState.Active;
            return new PointStatus { State = radio.State, Adapter = adapter, Clients = radio.Peers };
        }
        public void StartUsing(byte[] ssid, string password)
        {
            radio.Starts++;
            if (radio.LaunchError) throw new WifiException(Wlan.Describe(31, "WlanHostedNetworkStartUsing", 19));
            owned = true;
            if (radio.Delay == 0) radio.State = PointState.Active;
        }
        public void StopUsing()
        {
            if (radio.StopError) throw new WifiException(Wlan.Describe(1062, "WlanHostedNetworkStopUsing"));
            radio.Stops++;
            owned = false;
            radio.State = PointState.Idle;
        }
        public void Dispose()
        {
            if (!owned) return;
            radio.Releases++;
            owned = false;
            if (!radio.External) radio.State = PointState.Idle;
        }
    }
}
