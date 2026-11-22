using System.IO;
using System.Windows;

namespace Pomogator.Vpn;

public partial class App : Application
{
    private FileStream? instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--tun-helper")
        {
            if (Guid.TryParseExact(e.Args[1], "N", out var id) && int.TryParse(e.Args[2], out var parent) && parent > 0)
                Shutdown(await TunSession.RunWorker(id, parent));
            else Shutdown(1);
            return;
        }
        try { instance = LocalData.Lock(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Приложение уже открыто или папка настроек недоступна.", "ПОМОГАТОР VPN");
            Shutdown();
            return;
        }
        var window = new MainWindow { StartInTray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
