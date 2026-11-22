using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace PomogatorWifiLite
{
    internal static class Program
    {
        private const string Title = "ПОМОГАТОР Wi-Fi Lite";
        internal static string WindowMarker => @"Local\PomogatorWifi.UI." + SessionKey;
        internal static string SessionKey
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                using (var current = Process.GetCurrentProcess())
                    return id.User.Value + "." + current.SessionId;
            }
        }

        internal static bool PreviousRunning()
        {
            // The owner can outlive the visible window in the original application.
            if (Mutex.TryOpenExisting(@"Local\PomogatorWifi.Owner." + SessionKey, out var owner))
            {
                owner.Dispose();
                return true;
            }
            var previous = Process.GetProcessesByName("PomogatorWifi");
            foreach (var process in previous) process.Dispose();
            return previous.Length != 0;
        }

        [STAThread]
        private static void Main()
        {
            try
            {
                // Reserve the original window marker as well so it cannot open while Lite is running.
                using (var windowGuard = new Mutex(false, WindowMarker, out var first))
                {
                    if (!first || PreviousRunning())
                    {
                        MessageBox.Show("Остановите точку в её текущем приложении и закройте прежний ПОМОГАТОР Wi-Fi. " +
                            "Если Lite уже открыт, используйте его окно.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var window = new LiteWindow(new HostedSession(() => new Wlan(), PreviousRunning), new IcsProbe()))
                        Application.Run(window);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show("Не удалось открыть программу. Код 0x" + error.HResult.ToString("X8") + ".", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
