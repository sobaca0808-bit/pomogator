using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PomogatorNetworkDiagnostics;

if (args.Length == 1)
{
    switch (args[0])
    {
        case "--child-success":
            Console.Error.WriteLine("private test stderr");
            Console.WriteLine(Report.Json(Finding.Create(Outcome.Ok, "Проверено")));
            return 0;
        case "--child-timeout":
            Thread.Sleep(15000);
            return 0;
        case "--child-failure":
            Console.Error.WriteLine("private test stderr");
            return 23;
    }
    return 2;
}

var tests = new (string Name, Func<Task> Run)[] {
    ("Separate full, partial and failed reachability", Sync(() => {
        Check(Internet.Assess(2, 2) == Outcome.Ok);
        Check(Internet.Assess(1, 2) == Outcome.Review);
        Check(Internet.Assess(0, 2) == Outcome.Problem);
        Check(Internet.Assess(0, 0) == Outcome.Review);
    })),
    ("Redirect or unexpected HTTP control content is inconclusive", Sync(() => {
        Check(Internet.HttpOutcome(200, true, true) == Outcome.Ok);
        Check(Internet.HttpOutcome(200, true, false) == Outcome.Review);
        Check(Internet.HttpOutcome(302, true, false) == Outcome.Review);
        Check(Internet.HttpOutcome(302, false, false) == Outcome.Ok);
        Check(Internet.HttpOutcome(407, false, false) == Outcome.Review);
    })),
    ("Remove proxy user information and URL payload", Sync(() => {
        var url = new Uri("https://www.microsoft.com/");
        var proxy = Internet.ProxyFor(url, new Uri("http://name:secret@127.0.0.1:8080/path?token=a#b"));
        Check(proxy.AbsoluteUri == "http://127.0.0.1:8080/");
        Check(Internet.ProxyFor(url, url) == null);
        Check(Internet.ProxyFor(url, null) == null);
    })),
    ("Do not infer a network failure from COM alone", Sync(() => {
        var results = new[] { Create("ics", Outcome.Review, "0x80004002"), Create("ip", Outcome.Ok) };
        var summary = Interpretation.Analyze(results);
        Check(summary.Observed.Length == 1 && summary.Observed[0].Contains("COM"));
        Check(summary.Possible.Length == 0 && summary.Unverified.Length > 0);
    })),
    ("Record E_NOINTERFACE for one ICS connection and read another", Sync(() => {
        var refs = new List<object>();
        var details = new List<string>();
        var codes = new List<string>();
        var manager = new FixtureManager();
        Ics.ReadConnection(refs, manager, new FixtureConnection(true), 4, details, codes);
        Ics.ReadConnection(refs, manager, new FixtureConnection(false), 5, details, codes);
        Check(codes.SequenceEqual(new[] { "0x80004002" }));
        Check(details.Any(detail => detail.StartsWith("INetSharingConfigurationForINetConnection #4 (0x80004002)")));
        Check(details.Any(detail => detail.StartsWith("#5 ICS: получатель")));
    })),
    ("Flag DNS as a possible cause only when the observations support it", Sync(() => {
        var results = new[] { Create("ip", Outcome.Ok), Create("dns", Outcome.Problem) };
        Check(Interpretation.Analyze(results).Possible.Single().Contains("DNS"));
        results[0].Status = Outcome.Review;
        Check(Interpretation.Analyze(results).Possible.Length == 0);
    })),
    ("Compare successful direct and failed proxied requests", Sync(() => {
        var results = new[] { Create("direct", Outcome.Ok), Create("system-proxy", Outcome.Problem) };
        Check(Interpretation.Analyze(results).Possible.Single().Contains("прокси"));
        results[1].Status = Outcome.Review;
        Check(Interpretation.Analyze(results).Possible.Length == 0);
    })),
    ("Round-trip UTF-8 JSON report with timestamps, statuses and HRESULT", Sync(() => {
        var report = new Report { StartedAt = Report.Now(), FinishedAt = Report.Now(), WindowsVersion = "10.0",
            Results = new[] { Create("ics", Outcome.Review, "0x80004002") } };
        report.Conclusions = Interpretation.Analyze(report.Results);
        var decoded = Report.Parse<Report>(Report.Json(report));
        Check(decoded.StartedAt == report.StartedAt && decoded.FinishedAt == report.FinishedAt);
        Check(decoded.Results.Single().Status == Outcome.Review);
        Check(decoded.Results[0].Label == "Требуется проверка");
        Check(decoded.Results[0].Codes.Single() == "0x80004002");
        Check(decoded.ToText().Contains("Подтверждённые сбои проверок"));
        Check(decoded.ToText().Contains("Коды: 0x80004002"));
    })),
    ("Use UTF-16LE for Windows PowerShell EncodedCommand", Sync(() => {
        const string script = "Write-Output 'Проверка'";
        var encoded = Convert.FromBase64String(Runner.EncodePowerShell(script));
        Check(Encoding.Unicode.GetString(encoded) == script);
    })),
    ("Receive structured Cyrillic output and discard stderr", async () => {
        var output = await Runner.ReadProcess(Child("--child-success"), TimeSpan.FromSeconds(5));
        Check(!output.Contains("stderr"));
        Check(Report.Parse<Finding>(output).Summary == "Проверено");
    }),
    ("Enforce the worker deadline", async () => {
        var timer = Stopwatch.StartNew();
        try { await Runner.ReadProcess(Child("--child-timeout"), TimeSpan.FromMilliseconds(500)); }
        catch (TimeoutException)
        {
            Check(timer.Elapsed < TimeSpan.FromSeconds(5));
            return;
        }
        throw new InvalidOperationException("A hung worker was accepted.");
    }),
    ("Return the exit code without leaking worker stderr", async () => {
        try { await Runner.ReadProcess(Child("--child-failure"), TimeSpan.FromSeconds(5)); }
        catch (ProbeExitException error)
        {
            Check(error.ExitCode == 23 && !error.Message.Contains("stderr"));
            return;
        }
        throw new InvalidOperationException("Failed worker was accepted.");
    })
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine(tests.Length + " checks passed.");
return 0;


static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

static ProcessStartInfo Child(string flag) => new ProcessStartInfo(Environment.ProcessPath!, flag);

static Finding Create(string id, Outcome outcome, params string[] codes)
{
    var finding = Finding.Create(outcome, "Измерение", codes: codes);
    finding.Id = id;
    finding.Title = "Проверка " + id;
    finding.CheckedAt = Report.Now();
    finding.DurationMs = 10;
    finding.Label = Finding.LabelFor(outcome);
    return finding;
}

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Unexpected test result.");
}


sealed class FixtureConnection(bool missingConfig) : Ics.INetConnection
{
    public readonly bool MissingConfig = missingConfig;
}

sealed class FixtureManager : Ics.INetSharingManager
{
    public bool GetSharingInstalled() => true;
    public void _VtblGap1_2() => throw new NotSupportedException();
    public Ics.INetSharingEveryConnectionCollection GetEveryConnection() => throw new NotSupportedException();
    public Ics.INetConnectionProps GetConnectionProps(Ics.INetConnection connection) => new FixtureProps();

    public Ics.INetSharingConfiguration GetConfiguration(Ics.INetConnection connection)
    {
        if (((FixtureConnection)connection).MissingConfig)
            throw new COMException("private", unchecked((int)0x80004002));
        return new FixtureConfig();
    }
}

sealed class FixtureProps : Ics.INetConnectionProps
{
    public string GetGuid() => "10101010-1234-5678-1234-101010101010";
    public string GetName() => "Wi-Fi test";
    public int GetStatus() => 2;
    public int GetMediaType() => 3;
    public void _VtblGap1_1() => throw new NotSupportedException();
}

sealed class FixtureConfig : Ics.INetSharingConfiguration
{
    public bool GetSharingEnabled() => true;
    public int GetSharingConnectionType() => 1;
}
