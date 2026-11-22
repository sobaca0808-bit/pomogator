using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pomogator.Vpn;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var directory = args.Single();
        Directory.CreateDirectory(directory);
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow { Width = 980, Height = 690 };
        window.Show();
        Pump(window);
        window.ProfilesList.Add(Profiles.Parse("vless://10000000-0000-4000-8000-000000000001@server.example:443?security=tls")
            with { Name = "Тестовый сервер" });
        window.ProfilesList.Add(window.ProfilesList[0] with { Name = "Резервный сервер" });
        window.Servers.SelectedIndex = 0;

        CheckState(window, Phase.Disconnected, "Отключён", "–");
        Capture(window, directory, "01-disconnected", 96);
        window.Connection.Begin();
        window.Detail.Text = "Контрольный HTTPS-запрос через выбранный VLESS…";
        CheckState(window, Phase.Connecting, "Подключается", "…");
        Capture(window, directory, "02-connecting", 96);
        foreach (var check in Enum.GetValues<Check>()) window.Connection.Record(check, CheckResult.Passed);
        window.Connection.Confirm(DateTimeOffset.Now, false);
        window.Detail.Text = "HTTP 127.0.0.1:31852";
        CheckState(window, Phase.Connected, "Подключён и проверен", "✓");
        foreach (var (dpi, scale) in new[] { (96, 100), (120, 125), (144, 150) })
            Capture(window, directory, $"03-connected-{scale}", dpi);
        window.Connection.ReportHealth(false);
        CheckState(window, Phase.Unstable, "Связь нестабильна", "!");
        Capture(window, directory, "09-unstable", 96);
        window.Connection.ReportHealth(true);
        window.DiagnosticsPanel.IsExpanded = true;
        window.Height = 860;
        window.AddLog("Тестовое состояние интерфейса. HTTPS через VLESS проверен локальным стендом Windows CI.");
        Pump(window);
        window.ContentScroll.ScrollToBottom();
        Capture(window, directory, "04-diagnostics", 96);
        window.DiagnosticsPanel.IsExpanded = false;
        window.ContentScroll.ScrollToTop();
        window.Connection.BeginStop();
        CheckState(window, Phase.Disconnecting, "Отключается", "↘");
        Capture(window, directory, "05-disconnecting", 96);
        window.Connection.End(true);
        window.Detail.Text = "Локальный прокси перестал отвечать. Повторите подключение.";
        CheckState(window, Phase.Error, "Ошибка подключения", "!");
        Capture(window, directory, "06-error", 96);
        window.Connection.End(false);
        window.Detail.Text = "Нажмите круглую кнопку, чтобы подключиться.";

        window.WindowState = WindowState.Minimized;
        Pump(window);
        Assert(!window.IsVisible, "Minimize did not hide to tray.");
        window.ShowFromTray();
        Pump(window);
        Assert(window.IsVisible && window.WindowState == WindowState.Normal, "Tray restoration failed.");

        window.Width = 545;
        window.Height = 735;
        Pump(window);
        Assert(GridPosition(window.ServersPanel) == 1, "Server pane did not reflow.");
        Capture(window, directory, "07-narrow", 144);

        window.Width = 980;
        window.Height = 750;
        window.SettingsPanel.Visibility = Visibility.Visible;
        window.ContentScroll.ScrollToTop();
        Capture(window, directory, "08-settings", 96);
        window.Close();
        Console.WriteLine("PASS WPF: all six status labels, glyphs and tray phases, minimize/restore, narrow layout, 100/125/150% captures.");
    }

    private static int GridPosition(System.Windows.UIElement element) => System.Windows.Controls.Grid.GetRow(element);

    private static void CheckState(MainWindow window, Phase phase, string label, string glyph)
    {
        Pump(window);
        Assert(window.Status.Text == label && window.StatusGlyph.Text == glyph, $"Status rendering failed for {phase}.");
        Assert(window.Tray?.Current == phase, $"Tray state failed for {phase}.");
    }

    private static void Capture(MainWindow window, string path, string name, int dpi)
    {
        Pump(window);
        var scale = dpi / 96.0;
        var view = window.Shell;
        var width = (int)Math.Ceiling(view.ActualWidth * scale);
        var height = (int)Math.Ceiling(view.ActualHeight * scale);
        Assert(width > 0 && height > 0, "WPF capture dimensions are invalid.");
        var image = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        image.Render(view);
        var pixels = new byte[4];
        image.CopyPixels(new Int32Rect(5, 5, 1, 1), pixels, 4, 0);
        Assert(pixels[3] != 0, "WPF capture is transparent.");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(path, name + ".png"));
        png.Save(output);
    }

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
