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
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
