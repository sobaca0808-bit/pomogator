using System.IO;
using System.Windows;

namespace Pomogator.Vpn;

public partial class App : Application
{
    private FileStream? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
