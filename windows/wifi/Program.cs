using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace PomogatorWifi
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--wifi-owner") return Owner.Serve();
                if (args.Length == 3 && args[0] == "--wifi-ics" && Guid.TryParseExact(args[1], "N", out var pipe) &&
                    int.TryParse(args[2], out var parent)) return IcsWorker.Serve(pipe, parent);
                if (args.Length == 3 && args[0] == "--wifi-stop" && Guid.TryParseExact(args[1], "N", out var stopPipe) &&
                    int.TryParse(args[2], out var stopParent)) return HostedStopWorker.Serve(stopPipe, stopParent);
                using (var mutex = new Mutex(true, @"Local\PomogatorWifi.UI." + Wire.UserSession, out var first))
                {
                    if (!first) return 0;
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Local.Log("application_started");
                    using (var window = new WifiWindow())
                        Application.Run(window);
                }
                return 0;
            }
            catch (Exception error)
            {
                Local.LogError("application_failed", error);
                if (!args.Any(value => value.StartsWith("--wifi-", StringComparison.Ordinal)))
                    MessageBox.Show("Не удалось запустить ПОМОГАТОР Wi-Fi. Проверьте диагностический журнал в папке PomogatorWifi внутри %LOCALAPPDATA%.",
                        "ПОМОГАТОР Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
