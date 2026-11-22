using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PomogatorWifiLite;

internal static class Smoke
{
    [STAThread]
    private static void Main()
    {
        using (var marker = new Mutex(false, @"Local\PomogatorWifi.Owner." + Program.SessionKey, out var first))
        {
            Require(first && Program.PreviousRunning(), "original owner marker detection");
        }
        using (var previous = new Mutex(false, @"Local\PomogatorWifi.UI." + Program.SessionKey, out var opened))
        using (var lite = new Mutex(false, Program.WindowMarker, out var created))
            Require(opened && !created, "original window and Lite exclude each other");
        using (var form = new LiteWindow(new HostedSession(() => new Wlan(), () => false), new IcsProbe()))
        {
            var table = form.Controls.OfType<TableLayoutPanel>().Single();
            var actions = table.Controls.OfType<FlowLayoutPanel>().Single();
            var buttons = actions.Controls.OfType<Button>().Select(item => item.Text).ToArray();
            Require(buttons.SequenceEqual(new[] { "Запустить", "Остановить" }), "two main buttons");
            Require(table.Controls.OfType<TextBox>().Any(item => item.UseSystemPasswordChar), "password masking");
            Require(table.Controls.OfType<Label>().Any(item => item.Text.Contains("на подключённом телефоне")), "separate Internet check");
            form.CreateControl();
        }

        // The hosted runner may have neither a Wi-Fi radio nor WlanSvc.
        try
        {
            using (var service = new Wlan())
                Console.WriteLine("PASS Windows read-only WLAN call · " + service.Read().State);
        }
        catch (WifiException error)
        {
            Require(error.Message.Contains("Win32 "), "native WLAN code");
            Console.WriteLine("PASS Windows WLAN returns the native error without trying to start the service");
        }
        var read = new IcsProbe().BeginRead(Guid.Empty);
        Require(read.Wait(TimeSpan.FromSeconds(7)) && !string.IsNullOrWhiteSpace(read.Result), "read-only ICS status or error");
        Console.WriteLine("PASS WinForms, Windows WLAN and read-only ICS entry points");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
