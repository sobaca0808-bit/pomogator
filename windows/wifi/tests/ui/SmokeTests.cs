using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using PomogatorWifi;

internal static class SmokeTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var results = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(results);

        CheckProtectedSettings();
        CheckStartupRegistry();
        CheckTray();
        CheckWindowStates();
        CheckOwnerTransport();
        RenderScreens(results);
        return 0;
    }

    private static void CheckProtectedSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PomogatorWifiTest-" + Guid.NewGuid());
        var path = Path.Combine(folder, "profile");
        const string pass = "fixture-key-NotForUse";
        var defaults = new UserSettings();
        Require(!defaults.AutoLaunch && !defaults.AutoWifi && !defaults.CloseToTray && !defaults.AutoRestore, "automation defaults");
        try
        {
            for (var mask = 0; mask < 16; mask++)
            {
                var settings = new UserSettings { Ssid = "Тестовая сеть", Password = pass,
                    AutoLaunch = (mask & 1) != 0, AutoWifi = (mask & 2) != 0,
                    CloseToTray = (mask & 4) != 0, AutoRestore = (mask & 8) != 0 };
                Local.Save(path, settings);
                var encrypted = File.ReadAllBytes(path);
                Require(!Encoding.UTF8.GetString(encrypted).Contains(pass) &&
                    !Encoding.Unicode.GetString(encrypted).Contains(pass), "unencrypted password");
                var saved = Local.Load<UserSettings>(path);
                Require(saved.Password == pass && saved.Ssid == settings.Ssid && saved.AutoLaunch == settings.AutoLaunch &&
                    saved.AutoWifi == settings.AutoWifi && saved.CloseToTray == settings.CloseToTray &&
                    saved.AutoRestore == settings.AutoRestore, "independent settings");
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine("PASS Windows DPAPI, atomic replacement, and all sixteen automation combinations");
    }

    private static void CheckStartupRegistry()
    {
        var key = "PomogatorWifiSmoke-" + Guid.NewGuid();
        const string path = @"C:\Program Files\PomogatorWifi\PomogatorWifi.exe";
        try
        {
            Local.SetAutostart(true, path, key);
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                Require((string)run?.GetValue(key) == "\"" + path + "\" --logon", "startup command");
                Local.SetAutostart(false, path, key);
                Require(run.GetValue(key) == null, "startup setting removed");
            }
        }
        finally { Local.SetAutostart(false, path, key); }
        Console.WriteLine("PASS Windows Run registry set and remove with isolated value");
    }

    private static void CheckTray()
    {
        using (var window = new WifiWindow(testing: true))
        {
            window.EnableTrayForSmokeTest();
            window.Show();
            Application.DoEvents();
            Require(window.TrayVisible && window.Visible, "tray icon and window shown");
            window.SetTrayPreference(true);
            window.Close();
            Require(!window.Visible && !window.IsDisposed && window.TrayVisible, "close hides in tray");
            window.ShowWindow();
            Require(window.Visible && !window.IsDisposed, "reopening window");
            window.SetTrayPreference(false);
            window.Close();
            Require(window.IsDisposed, "close exits GUI with tray disabled");
        }
        Console.WriteLine("PASS Windows tray lifecycle and independent close preference");
    }

    private static WifiReport Demo(WifiPhase phase) => new WifiReport
    {
        Phase = phase, Ssid = "POMOGATOR", HasWifi = phase != WifiPhase.Unavailable,
        Pppoe = true, Peers = phase == WifiPhase.Owned || phase == WifiPhase.External ? (uint?)2 : null,
        Sharing = phase == WifiPhase.Owned || phase == WifiPhase.External ? SharingState.Verified : SharingState.Disabled,
        Mechanism = "Hosted Network", Mobile = MobilePhase.Off
    };

    private static void CheckWindowStates()
    {
        using (var window = new WifiWindow(testing: true))
        {
            foreach (WifiPhase phase in Enum.GetValues(typeof(WifiPhase)))
            {
                var demo = Demo(phase);
                window.Present(demo);
                Require(window.ShownPhase == phase, "phase presentation");
                Require(window.CanToggle == (phase == WifiPhase.Off || phase == WifiPhase.Owned), "power availability");
            }
        }
        using (var window = new WifiWindow(testing: true))
        {
            var legacy = Demo(WifiPhase.External);
            legacy.ExternalTarget = new HostedTarget { Adapter = Guid.NewGuid(), Bssid = "12-34-56-78-9A-BC", SsidBytes = "UE9NT0dBVE9S" };
            window.Present(legacy);
            Require(window.CanToggle, "manual OFF enabled for verified external Hosted Network");
            legacy.Phase = WifiPhase.Unconfirmed;
            window.Present(legacy);
            Require(window.CanToggle, "manual OFF can verify WLAN independently of a previous helper");
            legacy.ExternalTarget = null;
            window.Present(legacy);
            Require(!window.CanToggle, "manual OFF disabled without Hosted Network identity");
        }
        var checks = DiagnosticsWindow.Summarize(Demo(WifiPhase.External), true);
        Require(checks.Contains("внешний сеанс") && checks.Contains("подтверждены Windows"), "external session diagnostics");
        Console.WriteLine("PASS Russian status presentation and power control for all eight states");
    }

    private static void CheckOwnerTransport()
    {
        var adapter = Guid.NewGuid();
        var running = HostedNetwork.State.Idle;
        var stops = 0;
        var generation = 0L;
        var statusFailure = 0;
        var startFailure = 0;
        var renewed = 0;
        var now = DateTime.UtcNow;
        var name = "PomogatorWifi.Test." + Guid.NewGuid().ToString("N");
        HostedNetwork.State Fresh(out Guid id) { id = adapter; return running; }
        HostedNetwork.State Query(out Guid id)
        {
            var code = Volatile.Read(ref statusFailure);
            if (code != 0) throw new WlanFailure((uint)code, 0, "WlanHostedNetworkQueryStatus");
            return Fresh(out id);
        }
        var session = new OwnerSession(Query, () => Interlocked.Read(ref generation), () => 13,
            (ssid, key, automatic) =>
            {
                if (Volatile.Read(ref startFailure) != 0) throw new WlanFailure(31, 19, "WlanHostedNetworkStartUsing");
                running = HostedNetwork.State.Active;
            },
            () => { stops++; running = HostedNetwork.State.Idle; return new HostedRelease(0, 0); },
            (code, details) => { }, Fresh, () => { renewed++; Volatile.Write(ref statusFailure, 0); }, () => now);
        var process = Task.Run(() => Owner.ServeTransport(name, session, pipe => true, (code, details) => { }));
        OwnerReply Send(OwnerRequest command) => Owner.Exchange(name, command, 4000, pipe => true);

        var start = Send(new OwnerRequest { Command = "start", Origin = "window" });
        Require(start != null && start.Active, "pipe startup");
        using (var initial = new WifiWindow(testing: true))
        {
            initial.Present(Demo(WifiPhase.Owned));
            initial.Show();
            Application.DoEvents();
            initial.SetTrayPreference(true);
            initial.Close();
            Require(!initial.Visible && !initial.IsDisposed, "first GUI in tray");
            initial.SetTrayPreference(false);
            initial.Close();
        }

        // No UI has the pipe open and no status traffic keeps the session alive.
        var elapsed = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(61));
        var refreshed = Send(new OwnerRequest { Command = "status" });
        Require(refreshed != null && refreshed.Active && refreshed.Owner == start.Owner && elapsed.Elapsed.TotalSeconds >= 60,
            "same owned Windows pipe session after more than 60 seconds");
        using (var reopened = new WifiWindow(testing: true))
        {
            reopened.Present(Demo(WifiPhase.Unconfirmed));
            Require(!reopened.CanToggle, "no action while ownership is unknown");
            reopened.Present(Demo(WifiPhase.Owned));
            Require(reopened.CanToggle, "OFF enabled after refresh with the same session");
        }

        // Check that a disconnected client has no effect on the running session.
        using (var abandoned = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
            abandoned.Connect(4000);
        refreshed = Send(new OwnerRequest { Command = "status" });
        Require(refreshed != null && refreshed.Active && refreshed.Owner == start.Owner, "pipe survives disconnected client");

        running = HostedNetwork.State.Idle;
        Interlocked.Increment(ref generation);
        Volatile.Write(ref statusFailure, 5023);
        Volatile.Write(ref startFailure, 31);
        var recover = new OwnerRequest { Command = "recover", Owner = start.Owner, Origin = "window" };
        var failed = Send(recover);
        Require(failed?.Error != null && failed.Error.Contains("WLAN 31, причина 19") && renewed == 2 && stops == 0,
            "native start failure returned across the actual pipe");
        var repeated = Send(recover);
        Require(repeated?.Error != null && repeated.Error.Contains("Подождите") && renewed == 2, "cooldown prevents retry");
        now = now.AddSeconds(5);
        Volatile.Write(ref startFailure, 0);
        var restored = Send(recover);
        Require(restored != null && restored.Active && restored.Owner == start.Owner && renewed == 3, "fresh WLAN recovered");

        var stopped = Send(new OwnerRequest { Command = "stop", Owner = start.Owner, Adapter = adapter, Origin = "tray" });
        Require(stopped != null && stopped.Owner == Guid.Empty && !stopped.Active && stops == 1, "explicit OFF from tray");
        Require(process.Wait(4000), "helper transport completed after OFF");
        Console.WriteLine("PASS live Windows named pipe: start, tray/close, 61 seconds idle, reopen, simulated WLAN failure and recovery, OFF");
    }

    private static void RenderScreens(string output)
    {
        foreach (var percent in new[] { 100, 125, 150 })
            using (var window = new WifiWindow(testing: true))
            {
                window.Present(Demo(WifiPhase.Owned));
                Capture(window, output, "wifi-" + percent, percent);
            }
        using (var window = new WifiWindow(testing: true))
        {
            window.Present(Demo(WifiPhase.External));
            Capture(window, output, "wifi-external", 100);
        }
        using (var settings = new SettingsWindow(new UserSettings(), WifiPhase.Off))
            Capture(settings, output, "wifi-settings", 100);
        using (var diagnostics = new DiagnosticsWindow(Demo(WifiPhase.Owned), true, null))
            Capture(diagnostics, output, "wifi-diagnostics", 100);
        Console.WriteLine("PASS Windows WinForms renders six PNG previews, including simulated 100%, 125%, 150%");
    }

    private static void Capture(Form window, string output, string name, int percent)
    {
        window.Show();
        Application.DoEvents();
        if (percent != 100) window.Scale(new SizeF(percent / 100f, percent / 100f));
        window.PerformLayout();
        Application.DoEvents();
        Require(window.Controls.Count > 0 && window.ClientSize.Width > 450, "window layout");
        VerifyLayout(window);
        using (var picture = new Bitmap(window.Width, window.Height))
        {
            window.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
            Require(picture.GetPixel(30, 60) != picture.GetPixel(60, 125), "non-blank render");
            picture.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        }
        window.Hide();
    }

    private static void VerifyLayout(Control view)
    {
        foreach (Control child in view.Controls)
        {
            if (!child.Visible) continue;
            if (view is TableLayoutPanel table)
            {
                var cell = table.GetPositionFromControl(child);
                Require(cell.Column >= 0 && cell.Row >= 0, "control assigned to a table cell");
                Require(child.Width > 0 && child.Height > 0, "visible control size");
                if (child is Button button)
                {
                    Require(button.Width >= 29 && button.Height >= 23, "usable button size");
                    Require(button.Parent.ClientRectangle.Contains(button.Bounds), "button fully visible within its panel");
                    if (button.Text.Length > 0)
                        Require(button.Width >= TextRenderer.MeasureText(button.Text, button.Font).Width, "complete label " + button.Text);
                    var screen = button.PointToScreen(new Point(0, button.Height - 1));
                    if (button.FindForm() is WifiWindow main)
                        Require(main.ClientRectangle.Contains(main.PointToClient(screen)), "main button in viewport");
                }
            }
            VerifyLayout(child);
        }
    }

    private static void Require(bool satisfied, string description)
    {
        if (!satisfied) throw new InvalidOperationException(description);
    }
}
