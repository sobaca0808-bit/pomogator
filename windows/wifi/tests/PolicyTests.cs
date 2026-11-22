using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using PEReader = System.Reflection.PortableExecutable.PEReader;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using PomogatorWifi;

var source = new Connection { Id = Guid.NewGuid(), Name = "Мой провайдер", Media = MediaType.Pppoe, Status = ConnectionStatus.Connected };
var target = new Connection { Id = Guid.NewGuid(), Name = "Любое имя Wi-Fi", Media = MediaType.Lan, Status = ConnectionStatus.Connected };
var ethernet = new Connection { Id = Guid.NewGuid(), Name = "Обычный Ethernet", Media = MediaType.Lan, Status = ConnectionStatus.Connected };
var second = new Connection { Id = Guid.NewGuid(), Name = "Второй PPPoE", Media = MediaType.Pppoe, Status = ConnectionStatus.Connected };
var connections = new List<Connection> { ethernet, source, target };
var tests = new (string Name, Action Run)[] {
    ("Match Windows SDK COM interface GUIDs, vtable slots and marshaling", () => {
        CheckCom<INetSharingManager>("C08956B7-1CD3-11D1-B1C5-00805FC1270E", ComInterfaceType.InterfaceIsDual,
            "GetSharingInstalled", "_VtblGap1_2", "GetConfiguration", "GetEveryConnection", "GetConnectionProps");
        CheckCom<INetConnection>("C08956A1-1CD3-11D1-B1C5-00805FC1270E", ComInterfaceType.InterfaceIsIUnknown);
        CheckCom<INetSharingEveryConnectionCollection>("33C4643C-7811-46FA-A89A-768597BD7223",
            ComInterfaceType.InterfaceIsDual, "GetNewEnum");
        CheckCom<INetConnectionProps>("F4277C95-CE5B-463D-8167-5662D9BCAA72", ComInterfaceType.InterfaceIsDual,
            "GetGuid", "GetName", "_VtblGap1_1", "GetStatus", "GetMediaType", "GetCharacteristics");
        CheckCom<INetSharingConfiguration>("C08956B6-1CD3-11D1-B1C5-00805FC1270E", ComInterfaceType.InterfaceIsDual,
            "GetSharingEnabled", "GetSharingConnectionType", "DisableSharing", "EnableSharing");

        var getConfig = typeof(INetSharingManager).GetMethod("GetConfiguration");
        var parameters = getConfig.GetParameters();
        Check(getConfig.ReturnType == typeof(int) && getConfig.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig));
        Check(parameters[0].ParameterType == typeof(INetConnection));
        Check(parameters[1].IsOut && parameters[1].ParameterType == typeof(INetSharingConfiguration).MakeByRefType());
        Check(parameters.All(p => p.GetCustomAttribute<MarshalAsAttribute>().Value == UnmanagedType.Interface));
        var getProps = typeof(INetSharingManager).GetMethod("GetConnectionProps");
        Check(getProps.ReturnType == typeof(INetConnectionProps) &&
            getProps.GetParameters().Single().ParameterType == typeof(INetConnection));
        Check(typeof(INetSharingManager).GetMethod("GetEveryConnection").ReturnType == typeof(INetSharingEveryConnectionCollection));
        CheckMarshal<INetSharingManager>("GetSharingInstalled", UnmanagedType.VariantBool);
        CheckMarshal<INetSharingEveryConnectionCollection>("GetNewEnum", UnmanagedType.IUnknown);
        CheckMarshal<INetConnectionProps>("GetGuid", UnmanagedType.BStr);
        CheckMarshal<INetConnectionProps>("GetName", UnmanagedType.BStr);
        CheckMarshal<INetSharingConfiguration>("GetSharingEnabled", UnmanagedType.VariantBool);
        Check(typeof(INetConnectionProps).GetMethod("GetCharacteristics").ReturnType == typeof(uint));
        Check(typeof(INetSharingConfiguration).GetMethod("EnableSharing").GetParameters().Single().ParameterType == typeof(SharingRole));
        Check((int)SharingRole.Public == 0 && (int)SharingRole.Private == 1);
        Check(typeof(IEnumVARIANT).GUID == new Guid("00020404-0000-0000-C000-000000000046"));
    }),
    ("Continue reading through the reported fourth object with E_NOINTERFACE", () => {
        var system = new FixtureWindows();
        using var snapshot = system.Open();
        var diagnostic = snapshot.Issues.Single();
        Check(diagnostic.Unsupported);
        Check(diagnostic.Detail.Contains("INetSharingConfigurationForINetConnection #4"));
        Check(diagnostic.Detail.Contains("GUID=89150b9f-9b5c-11d1-a91f-00805fc1270e"));
        Check(diagnostic.Detail.Contains("HRESULT=0x80004002"));
        Check(snapshot.Connections.Exists(c => c.Id == system.Target.Id)); // target is enumerated fifth
        snapshot.RequireVerified(system.Source.Id, system.Target.Id);
        Check(Policy.ChooseSource(snapshot.Connections).Id == system.Source.Id);
        Reject(() => snapshot.RequireVerified(system.Source.Id, diagnostic.Id));
    }),
    ("Continue after an error but block ICS writes when another object's role is unknown", () => {
        var system = new FixtureWindows();
        system.ObjectFour.Ports.Flags = Characteristics.Shared;
        using var snapshot = system.Open();
        Check(!snapshot.Issues.Single().Unsupported);
        Check(snapshot.Connections.Exists(c => c.Id == system.Target.Id));
        Reject(() => system.Find());
        Check(system.Writes.Count == 0);
    }),
    ("Do not skip E_NOINTERFACE from a real adapter or another HRESULT", () => {
        var system = new FixtureWindows();
        system.ObjectFour.Ports.Media = MediaType.Lan;
        using (var snapshot = system.Open()) Check(!snapshot.Issues.Single().Unsupported);
        system.ObjectFour.Ports.Media = MediaType.None;
        system.ObjectFour.ReadResult = unchecked((int)0x80004005);
        using (var snapshot = system.Open()) Check(!snapshot.Issues.Single().Unsupported);
        system.ObjectFour.ReadResult = 0;
        system.ObjectFour.RoleReadFailure = unchecked((int)0x80004002);
        using (var snapshot = system.Open()) Check(!snapshot.Issues.Single().Unsupported);
    }),
    ("Find the virtual adapter by WLAN GUID after it appears", () => {
        var system = new FixtureWindows { ShowTarget = false };
        var attempts = 0;
        var session = system.Find(() => { attempts++; system.ShowTarget = true; });
        Check(attempts == 1 && session.Target == system.Target.Id && session.Source == system.Source.Id);
    }),
    ("Missing and disabled virtual adapters cannot reach ICS confirmation or writes", () => {
        var system = new FixtureWindows { ShowTarget = false };
        Reject(() => system.Find());
        system.ShowTarget = true;
        system.Target.Ports.Status = ConnectionStatus.HardwareDisabled;
        Reject(() => system.Find());
        Check(system.Writes.Count == 0);
    }),
    ("Confirmation is required and pair is checked again before changing ICS", () => {
        var system = new FixtureWindows();
        var session = system.Find();
        Check(session.ConfigureSharing(pair => {
            Check(pair.Contains(system.Source.Ports.Name) && pair.Contains(system.Target.Ports.Name));
            return false;
        }) == SharingResult.Declined);
        Check(system.Writes.Count == 0);
        Reject(() => session.ConfigureSharing(pair => {
            system.Target.Ports.Status = ConnectionStatus.HardwareDisabled;
            return true;
        }));
        Check(system.Writes.Count == 0);
    }),
    ("Verify PPPoE public and Hosted Network private before reporting success", () => {
        var system = new FixtureWindows();
        var session = system.Find();
        Check(session.ConfigureSharing(pair => true) == SharingResult.Verified);
        Check(system.Writes.SequenceEqual(new[] { "enable Public", "enable Private" }));
        using (var snapshot = system.Open()) Check(snapshot.IsSharing(session.Source, session.Target));
        session.Restore();
        Check(system.Writes.SequenceEqual(new[] { "enable Public", "enable Private", "disable Private", "disable Public" }));
    }),
    ("Verify the same PPPoE GUID if Windows reports it as a shared WAN", () => {
        var system = new FixtureWindows();
        system.Source.MediaAfterEnable = MediaType.SharedAccessHostRas;
        var session = system.Find();
        Check(session.ConfigureSharing(pair => true) == SharingResult.Verified);
        using var snapshot = system.Open();
        Check(snapshot.IsSharing(session.Source, session.Target));
        Reject(() => Policy.ChooseSource(snapshot.Connections));
    }),
    ("Show native ICS operation and error and roll back partial sharing on failure", () => {
        var system = new FixtureWindows();
        system.Target.EnableFailure = unchecked((int)0x80070005);
        var session = system.Find();
        var error = Expect<IcsFailure>(() => session.ConfigureSharing(pair => true));
        var visible = Session.SetupFailureMessage(error);
        Check(visible.StartsWith("Настройка ICS не завершена."));
        Check(visible.Contains("EnableSharing(Private)") && visible.Contains("0x80070005"));
        Check(visible.Contains(system.Target.Id.ToString()));
        session.Restore();
        Check(system.Source.Role == null && system.Target.Role == null);
    }),
    ("Successful COM calls are insufficient when Windows does not confirm both roles", () => {
        var system = new FixtureWindows();
        system.Target.IgnoreEnable = true;
        var session = system.Find();
        var error = Expect<WifiError>(() => session.ConfigureSharing(pair => true));
        Check(Session.SetupFailureMessage(error).Contains("Windows не подтвердила роли ICS"));
        Check(system.Writes.SequenceEqual(new[] { "enable Public", "enable Private" }));
        using var snapshot = system.Open();
        Check(!snapshot.IsSharing(session.Source, session.Target));
    }),
    ("Treat only S_FALSE as a successful end to enumeration", () => {
        Check(IcsEnumeration.HasItem(0));
        Check(!IcsEnumeration.HasItem(1));
        int noInterface = unchecked((int)0x80004002);
        try { IcsEnumeration.HasItem(noInterface); }
        catch (COMException error) when (error.HResult == noInterface) { return; }
        throw new InvalidOperationException("Enumeration hid the native error");
    }),
    ("Retain the exact operation and HRESULT for a failed interface query", () => {
        const string operation = "QueryInterface(INetConnection) #2";
        var original = new InvalidCastException("external diagnostic");
        var error = new IcsFailure(operation, original);
        Check(error.Operation == operation && error.HResult == original.HResult);
        Check(error.InnerException == original && error.Message.Contains("0x80004002"));
        Check(!error.Message.Contains(original.Message));
    }),
    ("Detect PPPoE by type and state rather than name", () => Check(Policy.ChooseSource(connections) == source)),
    ("Reject missing PPPoE", () => Reject(() => Policy.ChooseSource(new[] { ethernet }))),
    ("Reject multiple active PPPoE links", () => Reject(() => Policy.ChooseSource(new[] { source, second }))),
    ("Accept a verified new pair", () => Check(Policy.NeedsSharing(connections, source.Id, target.Id))),
    ("Reject a missing virtual adapter", () => Reject(() => Policy.NeedsSharing(connections, source.Id, Guid.NewGuid()))),
    ("Reject identical upstream and downstream", () => Reject(() => Policy.NeedsSharing(connections, source.Id, source.Id))),
    ("Preserve unrelated ICS configuration", () => {
        ethernet.Sharing = SharingRole.Public;
        try {
            Reject(() => Policy.NeedsSharing(connections, source.Id, target.Id));
            Check(!Policy.CanRestore(connections, source.Id, target.Id, true, true));
        } finally { ethernet.Sharing = null; }
    }),
    ("Reuse exact existing pair and leave it untouched", () => {
        source.Sharing = SharingRole.Public;
        target.Sharing = SharingRole.Private;
        try {
            Check(!Policy.NeedsSharing(connections, source.Id, target.Id));
            Check(!Policy.CanRestore(connections, source.Id, target.Id, false, false));
            Check(Policy.CanRestore(connections, source.Id, target.Id, true, true));
        } finally { source.Sharing = target.Sharing = null; }
    }),
    ("Permit rollback of an incomplete pair that the utility attempted", () => {
        source.Sharing = SharingRole.Public;
        try {
            Reject(() => Policy.NeedsSharing(connections, source.Id, target.Id));
            Check(Policy.CanRestore(connections, source.Id, target.Id, true, true));
        } finally { source.Sharing = null; }
    }),
    ("Distinguish inactive, owned Hosted Network, external Hosted Network, and mobile hotspot", () => {
        Check(WifiRules.Phase(true, HostedNetwork.State.Idle, false, MobilePhase.Off, false) == WifiPhase.Off);
        Check(WifiRules.Phase(true, HostedNetwork.State.Active, true, MobilePhase.Off, false) == WifiPhase.Owned);
        Check(WifiRules.Phase(true, HostedNetwork.State.Active, false, MobilePhase.Off, false) == WifiPhase.External);
        Check(WifiRules.Phase(true, HostedNetwork.State.Idle, false, MobilePhase.On, false) == WifiPhase.External);
        Check(WifiRules.Phase(true, HostedNetwork.State.Idle, false, MobilePhase.Transition, false) == WifiPhase.External);
        Check(WifiRules.Phase(true, HostedNetwork.State.Unavailable, false, MobilePhase.Unknown, true) == WifiPhase.External);
        Check(WifiRules.Phase(false, HostedNetwork.State.Unavailable, false, MobilePhase.Unknown, false) == WifiPhase.Unavailable);
    }),
    ("Auto start requires saved credentials and an idle point; no start beside external sharing", () => {
        Check(WifiRules.AutoStart(true, WifiPhase.Off, true));
        Check(!WifiRules.AutoStart(false, WifiPhase.Off, true));
        Check(!WifiRules.AutoStart(true, WifiPhase.Off, false));
        foreach (var phase in new[] { WifiPhase.External, WifiPhase.Owned, WifiPhase.Unavailable })
            Check(!WifiRules.AutoStart(true, phase, true));
    }),
    ("Auto recovery needs an observed outage and resumes once after PPPoE returns", () => {
        var edge = new RecoveryEdge();
        Check(!edge.Observe(true, true, true));
        Check(!edge.Observe(true, true, false));
        Check(!edge.Observe(true, true, null));
        Check(edge.Observe(true, true, true));
        Check(!edge.Observe(true, true, true));
        Check(!edge.Observe(true, true, false));
        Check(!edge.Observe(true, false, true));
        Check(!edge.Observe(true, true, true));
        Check(!edge.Observe(false, true, false));
        Check(!edge.Observe(true, true, true));
        Check(WifiRules.AutomaticTarget(target.Id, target.Id));
        Check(!WifiRules.AutomaticTarget(target.Id, source.Id));
        Check(!WifiRules.AutomaticTarget(Guid.Empty, Guid.Empty));
    }),
    ("Restore consented ICS roles only and leave a pair changed outside the utility untouched", () => {
        var system = new FixtureWindows();
        var session = system.Find();
        Check(session.ConfigureSharing(pair => true) == SharingResult.Verified);
        system.ObjectFour.ReadResult = 0;
        system.ObjectFour.Role = SharingRole.Private;
        var writes = system.Writes.Count;
        Reject(() => session.RestoreOwnedPair());
        Check(system.Writes.Count == writes);
        system.ObjectFour.Role = null;
        session.RestoreOwnedPair();
        Check(system.Target.Role == null && system.Source.Role == null);
    }),
    ("Only the original PPPoE and target can be repaired after a disconnect", () => {
        var system = new FixtureWindows();
        var session = system.Find();
        Check(session.ConfigureSharing(pair => true) == SharingResult.Verified);
        system.Source.Ports.Status = ConnectionStatus.Disconnected;
        system.Target.Role = null;
        var writes = system.Writes.Count;
        Reject(() => session.ConfigureSharing(_ => true, repair: true));
        Check(writes == system.Writes.Count);
        system.Source.Ports.Status = ConnectionStatus.Connected;
        Check(session.ConfigureSharing(_ => true, repair: true) == SharingResult.Verified);
        Check(system.Writes.Count == writes + 1);
        Check(system.Writes.Last() == "enable Private");
    }),
    ("Keep an owned handle across refreshes and require an explicit OFF for the same adapter", () => {
        var radio = new RadioFixture();
        var initial = radio.Start();
        Check(initial.Active && initial.Owner != Guid.Empty);
        for (var i = 0; i < 50; i++)
        {
            var status = radio.Session.Handle(new OwnerRequest { Command = "status" });
            Check(status.Active && status.Owner == initial.Owner && status.Adapter == initial.Adapter);
        }
        Check(radio.Stops == 0 && radio.Session.Retained);
        Reject(() => radio.Session.Handle(new OwnerRequest { Command = "stop", Owner = initial.Owner, Adapter = initial.Adapter }));
        Reject(() => radio.Session.Handle(new OwnerRequest { Command = "stop", Owner = initial.Owner, Adapter = initial.Adapter, Origin = "automation" }));
        Reject(() => radio.Session.Handle(new OwnerRequest { Command = "stop", Owner = initial.Owner, Adapter = Guid.NewGuid(), Origin = "window" }));
        Check(radio.Stops == 0);
        var stopped = radio.Off(initial);
        Check(radio.Stops == 1 && !radio.Session.Retained && radio.Session.Stopping && stopped.Owner == Guid.Empty);
        Check(radio.Events.Any(e => e.Contains("hosted_released source=window win32=0 reason=0 state=1")));
    }),
    ("A late refresh cannot reverse the button intent or substitute a different Wi-Fi session", () => {
        Check(WifiRules.SamePowerIntent(WifiPhase.Owned, source.Id, target.Id, WifiPhase.Owned, source.Id, target.Id));
        Check(!WifiRules.SamePowerIntent(WifiPhase.Off, Guid.Empty, Guid.Empty, WifiPhase.Owned, source.Id, target.Id));
        Check(!WifiRules.SamePowerIntent(WifiPhase.Owned, source.Id, target.Id, WifiPhase.Off, source.Id, target.Id));
        Check(!WifiRules.SamePowerIntent(WifiPhase.Owned, source.Id, target.Id, WifiPhase.Owned, target.Id, target.Id));
        Check(!WifiRules.SamePowerIntent(WifiPhase.Owned, source.Id, target.Id, WifiPhase.Owned, source.Id, source.Id));
        Check(WifiRules.Phase(true, HostedNetwork.State.Active, false, MobilePhase.Off, false, false) == WifiPhase.Unconfirmed);
        Check(WifiRules.Phase(true, HostedNetwork.State.Active, false, MobilePhase.Off, false) == WifiPhase.External);
    }),
    ("An unrelated reference can keep the access point active after releasing only our own", () => {
        var radio = new RadioFixture();
        var ours = radio.Start();
        radio.OtherReference = true;
        var after = radio.Off(ours);
        Check(after.WindowsState == (int)HostedNetwork.State.Active && !after.Active && after.Owner == Guid.Empty);
        Check(radio.Stops == 1 && radio.Events.Any(item => item.Contains("hosted_released") && item.Contains("state=2")));
        Reject(() => radio.Off(ours));
        Check(radio.Stops == 1);
    }),
    ("A same-GUID access point restarted by Windows or another client stays unowned", () => {
        var radio = new RadioFixture();
        var ours = radio.Start();
        radio.Gone++;
        var external = radio.Session.Observe();
        Check(external.Owner == ours.Owner && external.Adapter == ours.Adapter && !external.Active);
        Reject(() => radio.Off(ours));
        Reject(() => radio.Recover(ours));
        Check(radio.Stops == 0 && radio.Events.Any(item => item.Contains("windows_notification reason=13")));
    }),
    ("Resume only an idle point belonging to the retained token after sleep", () => {
        var radio = new RadioFixture();
        var ours = radio.Start();
        radio.State = HostedNetwork.State.Idle;
        radio.Gone++;
        Check(!radio.Session.Observe().Active);
        var resumed = radio.Recover(ours);
        Check(resumed.Active && resumed.Owner == ours.Owner && resumed.Adapter == ours.Adapter);
        Check(radio.Off(resumed).Owner == Guid.Empty);

        var edge = new RecoveryEdge();
        edge.Resumed(ours.Owner);
        Check(!edge.Observe(true, ours.Owner, null, true));
        Check(!edge.Observe(true, ours.Owner, true, false));
        Check(edge.Observe(true, ours.Owner, true, true));
        Check(!edge.Observe(true, ours.Owner, true, true));
        edge.Resumed(ours.Owner);
        Check(!edge.Observe(true, Guid.NewGuid(), true, true));
    }),
    ("A failed WLAN read or stop preserves the token and does not claim that the point stopped", () => {
        var radio = new RadioFixture();
        var ours = radio.Start();
        radio.FailRead = true;
        var unknown = radio.Session.Observe();
        Check(unknown.Owner == ours.Owner && unknown.WindowsState == null && unknown.Error != null);
        Reject(() => radio.Off(ours));
        radio.FailRead = false;
        radio.FailStop = true;
        Reject(() => radio.Off(ours));
        Check(radio.Session.Observe().Active && radio.Stops == 0 && !radio.Events.Any(item => item.Contains("hosted_released")));
        radio.FailStop = false;
        Check(radio.Off(ours).Owner == Guid.Empty && radio.Stops == 1);
        Check(radio.Events.Any(item => item.Contains("hosted_status_restored")));
    }),
    ("Validate SSID in UTF-8 bytes and passphrase in printable ASCII", () => {
        Check(Policy.EncodeSsid(new string('Ж', 16)).Length == 32);
        Reject(() => Policy.EncodeSsid(new string('Ж', 17)));
        Reject(() => Policy.EncodeSsid("line\nbreak"));
        Policy.CheckPassword(new string('a', 8));
        Policy.CheckPassword(new string('~', 63));
        Reject(() => Policy.CheckPassword(new string('a', 7)));
        Reject(() => Policy.CheckPassword(new string('a', 64)));
        Reject(() => Policy.CheckPassword("кириллица"));
    })
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"{tests.Length} checks passed.");

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Unexpected result");
}

static void Reject(Action action) => Expect<WifiError>(action);

static T Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T error) { return error; }
    throw new InvalidOperationException("Operation unexpectedly succeeded");
}

static void CheckCom<T>(string iid, ComInterfaceType type, params string[] methods)
{
    Check(typeof(T).IsImport && typeof(T).GUID == new Guid(iid));
    Check(typeof(T).GetCustomAttribute<InterfaceTypeAttribute>().Value == type);
    // At runtime reflection hides the reserved slots, so inspect the Assembly metadata.
    using var executable = File.OpenRead(typeof(T).Assembly.Location);
    using var image = new PEReader(executable);
    var metadata = image.GetMetadataReader();
    var definition = metadata.GetTypeDefinition(metadata.TypeDefinitions.Single(handle =>
        metadata.GetString(metadata.GetTypeDefinition(handle).Name) == typeof(T).Name));
    var declared = definition.GetMethods().Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name));
    Check(declared.SequenceEqual(methods));
}

static void CheckMarshal<T>(string method, UnmanagedType type) =>
    Check(typeof(T).GetMethod(method).ReturnParameter.GetCustomAttribute<MarshalAsAttribute>().Value == type);

sealed class FixtureWindows : INetSharingManager
{
    internal readonly List<string> Writes = new List<string>();
    internal readonly FixtureConnection Source;
    internal readonly FixtureConnection Target;
    internal readonly FixtureConnection ObjectFour;
    private readonly FixtureConnection[] connections;
    internal bool ShowTarget = true;

    internal FixtureWindows()
    {
        Source = new FixtureConnection(Writes, "Высокоскоростное подключение", MediaType.Pppoe, ConnectionStatus.Connected);
        Target = new FixtureConnection(Writes, "Подключение по локальной сети* 12", MediaType.Lan, ConnectionStatus.Connected);
        ObjectFour = new FixtureConnection(Writes, "Неизвестный объект", MediaType.None, ConnectionStatus.Disconnected);
        ObjectFour.Id = new Guid("89150b9f-9b5c-11d1-a91f-00805fc1270e");
        ObjectFour.ReadResult = unchecked((int)0x80004002);
        connections = new[] {
            Source,
            new FixtureConnection(Writes, "Wi-Fi", MediaType.Lan, ConnectionStatus.Connected),
            new FixtureConnection(Writes, "Ethernet", MediaType.Lan, ConnectionStatus.Disconnected),
            ObjectFour, Target
        };
    }

    internal Session Find(Action pause = null) => Session.Find(Query, Open, pause ?? (() => { }));
    internal IcsSnapshot Open() => new IcsSnapshot(this);

    private HostedNetwork.State Query(out Guid adapter)
    {
        adapter = Target.Id;
        return HostedNetwork.State.Active;
    }

    public bool GetSharingInstalled() => true;
    public void _VtblGap1_2() => throw new NotSupportedException();

    public int GetConfiguration(INetConnection connection, out INetSharingConfiguration configuration)
    {
        var fixture = (FixtureConnection)connection;
        configuration = fixture.ReadResult == 0 ? fixture : null;
        return fixture.ReadResult;
    }

    public INetSharingEveryConnectionCollection GetEveryConnection() =>
        new FixtureCollection(connections.Where(c => c != Target || ShowTarget).ToArray());

    public INetConnectionProps GetConnectionProps(INetConnection connection) => ((FixtureConnection)connection).Ports;
}

sealed class FixtureCollection(INetConnection[] connections) : INetSharingEveryConnectionCollection, IEnumVARIANT
{
    private int index;
    public object GetNewEnum() => this;
    public int Next(int celt, object[] items, IntPtr fetched)
    {
        if (index == connections.Length) return 1;
        items[0] = connections[index++];
        return 0;
    }
    public int Skip(int celt) => throw new NotSupportedException();
    public int Reset() => throw new NotSupportedException();
    public IEnumVARIANT Clone() => throw new NotSupportedException();
}

sealed class FixtureConnection : INetConnection, INetSharingConfiguration
{
    private readonly List<string> writes;
    internal Guid Id = Guid.NewGuid();
    internal readonly FixtureProps Ports;
    internal int ReadResult;
    internal int RoleReadFailure;
    internal int EnableFailure;
    internal bool IgnoreEnable;
    internal MediaType? MediaAfterEnable;
    internal SharingRole? Role;

    internal FixtureConnection(List<string> writes, string name, MediaType media, ConnectionStatus status)
    {
        this.writes = writes;
        Ports = new FixtureProps(this, name, media, status);
    }

    public bool GetSharingEnabled() => RoleReadFailure == 0
        ? Role.HasValue : throw new COMException("synthetic role read failure", RoleReadFailure);
    public SharingRole GetSharingConnectionType() => Role.Value;
    public void DisableSharing()
    {
        writes.Add("disable " + Role);
        Role = null;
    }
    public void EnableSharing(SharingRole role)
    {
        writes.Add("enable " + role);
        if (EnableFailure != 0) throw new COMException("synthetic configuration failure", EnableFailure);
        if (!IgnoreEnable) Role = role;
        if (MediaAfterEnable.HasValue) Ports.Media = MediaAfterEnable.Value;
    }
}

sealed class FixtureProps(FixtureConnection connection, string name, MediaType media, ConnectionStatus status) : INetConnectionProps
{
    internal readonly string Name = name;
    internal MediaType Media = media;
    internal ConnectionStatus Status = status;
    internal Characteristics Flags;

    public string GetGuid() => connection.Id.ToString();
    public string GetName() => Name;
    public void _VtblGap1_1() => throw new NotSupportedException();
    public int GetStatus() => (int)Status;
    public int GetMediaType() => (int)Media;
    public uint GetCharacteristics() => (uint)Flags;
}

internal sealed class RadioFixture
{
    internal HostedNetwork.State State = HostedNetwork.State.Idle;
    internal long Gone;
    internal bool OtherReference, FailRead, FailStop;
    internal int Stops;
    internal readonly Guid Adapter = Guid.NewGuid();
    internal readonly List<string> Events = new();
    internal readonly OwnerSession Session;

    internal RadioFixture()
    {
        Session = new OwnerSession(Query, () => Gone, () => 13, (name, password, automatic) => {
            if (State != HostedNetwork.State.Idle) throw new WifiError("Busy");
            State = HostedNetwork.State.Active;
        }, () => {
            if (FailStop) throw new WlanFailure(5, 5);
            Stops++;
            State = OtherReference ? HostedNetwork.State.Active : HostedNetwork.State.Idle;
            return new HostedRelease(0, 0);
        }, (code, detail) => Events.Add(code + " " + detail));
    }

    private HostedNetwork.State Query(out Guid adapter)
    {
        if (FailRead) throw new WlanFailure(31, 0);
        adapter = Adapter;
        return State;
    }

    internal OwnerReply Start() => Session.Handle(new OwnerRequest { Command = "start", Origin = "window" });
    internal OwnerReply Off(OwnerReply owner) => Session.Handle(new OwnerRequest {
        Command = "stop", Owner = owner.Owner, Adapter = owner.Adapter, Origin = "window"
    });
    internal OwnerReply Recover(OwnerReply owner) => Session.Handle(new OwnerRequest {
        Command = "recover", Owner = owner.Owner, Origin = "automation", Automatic = true
    });
}
