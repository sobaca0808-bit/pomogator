using System;
using System.Collections.Generic;
using PomogatorWifi;

var source = new Connection { Id = Guid.NewGuid(), Name = "Мой провайдер", IsPppoe = true, Connected = true };
var target = new Connection { Id = Guid.NewGuid(), Name = "Любое имя Wi-Fi", Connected = true };
var ethernet = new Connection { Id = Guid.NewGuid(), Name = "Обычный Ethernet", Connected = true };
var second = new Connection { Id = Guid.NewGuid(), Name = "Второй PPPoE", IsPppoe = true, Connected = true };
var connections = new List<Connection> { ethernet, source, target };
var tests = new (string Name, Action Run)[] {
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

static void Reject(Action action)
{
    try { action(); }
    catch (WifiError) { return; }
    throw new InvalidOperationException("Unsafe or invalid configuration was accepted");
}
