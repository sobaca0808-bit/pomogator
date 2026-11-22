using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pomogator.Vpn;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Profile> profiles = new();
    private readonly SystemProxy proxy = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<string> journal = new();
    private Xray? core;
    private bool busy;
    private bool storageReady;
    private bool closeRequested;
    private CancellationTokenSource? operation;

    public MainWindow()
    {
        InitializeComponent();
        Servers.ItemsSource = profiles;
        Loaded += Ready;
        Closing += ClosingWindow;
        timer.Tick += Monitor;
    }

    private void Ready(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var profile in LocalData.Read<Profile[]>(LocalData.Profiles) ?? Array.Empty<Profile>())
                profiles.Add(profile);
            storageReady = true;
            if (profiles.Count != 0) Servers.SelectedIndex = 0;
        }
        catch { ReportProblem("Не удалось расшифровать список серверов. Существующий файл сохранён."); }
        if (proxy.HasRecovery && MessageBox.Show(this,
            "Есть запись предыдущего сеанса. Восстановить исходный системный прокси, если параметры всё ещё совпадают с настройками ПОМОГАТОР?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            RestoreProxy(fromDisk: true);
        timer.Start();
        UpdateControls();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) UpdateControls();
    }

    private void UpdateControls()
    {
        var connected = core != null;
        var selected = Servers.SelectedItem is Profile;
        Connect.Content = busy && operation != null ? "Отменить" : connected ? "Отключиться" : "Подключиться";
        Connect.IsEnabled = busy ? operation != null : connected || selected;
        Servers.IsEnabled = !busy && !connected;
        Modes.IsEnabled = !busy && !connected;
        SystemMode.IsEnabled = !proxy.HasRecovery;
        if (!SystemMode.IsEnabled && !connected) LocalMode.IsChecked = true;
        Paste.IsEnabled = Import.IsEnabled = Subscribe.IsEnabled = storageReady && !busy && !connected;
        Remove.IsEnabled = Paste.IsEnabled && selected;
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            operation?.Cancel();
            return;
        }
        if (core != null)
        {
            Disconnect();
            return;
        }
        if (Servers.SelectedItem is not Profile profile) return;

        var systemMode = SystemMode.IsChecked == true;
        if (systemMode && MessageBox.Show(this,
            "Включить системный прокси Windows для текущего пользователя после проверки VPN? " +
            "Будут изменены только параметры локальной сети WinINet. Исходные значения сохранятся для восстановления.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        operation = new CancellationTokenSource();
        busy = true;
        UpdateControls();
        try
        {
            Status.Text = "Подключение…";
            Detail.Text = "Проверка адреса и TCP-порта VPN-сервера.";
            await Xray.CheckServerAsync(profile, operation.Token);
            AddLog("TCP-порт VPN-сервера доступен.");

            var path = Path.Combine(AppContext.BaseDirectory, "xray.exe");
            if (!File.Exists(path)) throw new VpnError("Рядом с приложением отсутствует xray.exe. Распакуйте полный комплект.");
            var port = Xray.FreePort();
            Detail.Text = "Запуск локального прокси.";
            core = await Xray.StartAsync(path, XrayConfig.Create(profile, port), port, operation.Token,
                WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback);
            AddLog("Ядро открыло локальный HTTP-прокси.");
            Detail.Text = "Проверка HTTPS и доменного имени через прокси.";
            await Xray.VerifyAsync(port, operation.Token);
            AddLog("Контрольный HTTPS-запрос через VLESS вернул HTTP 204.");
            operation.Token.ThrowIfCancellationRequested();
            if (!core.Running) throw new VpnError("Ядро завершилось после проверки.");
            if (systemMode)
            {
                proxy.Enable(port);
                AddLog("Системный прокси включён для текущего пользователя.");
            }
            Status.Text = "Подключено";
            Detail.Text = $"HTTP-прокси 127.0.0.1:{port}" +
                (systemMode ? ". Параметры системного прокси применены." : ". Укажите этот адрес в нужном приложении.");
        }
        catch (OperationCanceledException)
        {
            Disconnect();
            AddLog("Подключение отменено.");
        }
        catch (Exception error)
        {
            Disconnect();
            ReportProblem(error is VpnError ? error.Message : "Windows или ядро отклонили операцию. Код " + Code(error) + ".");
        }
        finally
        {
            busy = false;
            operation.Dispose();
            operation = null;
            UpdateControls();
            if (closeRequested) Close();
        }
    }

    private void Disconnect()
    {
        var restored = RestoreProxy();
        bool stopped;
        try { core?.Dispose(); stopped = true; }
        catch (Exception error)
        {
            stopped = false;
            AddLog("Ошибка остановки ядра. Код " + Code(error) + ".");
        }
        core = null;
        Status.Text = "Отключено";
        Detail.Text = restored && stopped ? "Сеанс завершён." : "Проверьте журнал и системные параметры.";
        AddLog("Отключение.");
        UpdateControls();
    }

    private bool RestoreProxy(bool fromDisk = false)
    {
        if (!fromDisk && !proxy.Active) return true;
        try
        {
            var result = proxy.Restore(fromDisk);
            AddLog(result switch {
                RestoreResult.Restored => "Исходный системный прокси восстановлен.",
                RestoreResult.ChangedElsewhere => "Системный прокси изменён извне. Текущие настройки сохранены.",
                _ => "Системный прокси уже в исходном состоянии."
            });
            return true;
        }
        catch (Exception error)
        {
            AddLog("Восстановление прокси не подтверждено. Запись сохранена. Код " + Code(error) + ".");
            return false;
        }
    }

    private void Monitor(object? sender, EventArgs e)
    {
        if (busy || core == null) return;
        if (!core.Running)
        {
            Disconnect();
            ReportProblem("Процесс ядра завершился.");
            return;
        }
        try
        {
            if (proxy.ReleaseIfChanged())
                ReportProblem("Системный прокси изменился извне. Изменения сохранены, ядро работает в локальном режиме.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Disconnect();
            ReportProblem("Не удалось проверить системный прокси. Код " + Code(error) + ".");
        }
    }

    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            closeRequested = true;
            operation?.Cancel();
            return;
        }
        timer.Stop();
        if (core != null || proxy.Active) Disconnect();
    }

    private void PasteClick(object sender, RoutedEventArgs e)
    {
        try { AddProfiles(Profiles.ParseList(Clipboard.GetText())); }
        catch (Exception error) { ImportError(error); }
    }

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Профили VLESS (*.txt;*.vless;*.json)|*.txt;*.vless;*.json|Все файлы|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync(async token =>
        {
            await using var file = File.OpenRead(dialog.FileName);
            return await Profiles.ReadListAsync(file, token);
        });
    }

    private async void SubscribeClick(object sender, RoutedEventArgs e)
    {
        var field = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
        var button = new Button { Content = "Загрузить", IsDefault = true };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Вставьте URL подписки HTTPS.", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(field);
        panel.Children.Add(button);
        var dialog = new Window { Owner = this, Title = "Импорт подписки", SizeToContent = SizeToContent.Height,
            Width = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        button.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => field.Focus();
        if (dialog.ShowDialog() != true) return;
        var address = field.Password.Trim();
        field.Clear();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            ReportProblem("Нужен URL HTTPS без имени пользователя, пароля и фрагмента.");
            return;
        }
        await ImportAsync(async token =>
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
            using var http = new HttpClient(handler);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new VpnError("Поставщик подписки вернул HTTP " + (int)response.StatusCode + ".");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await Profiles.ReadListAsync(stream, token);
        });
    }

    private async Task ImportAsync(Func<CancellationToken, Task<string>> load)
    {
        busy = true;
        UpdateControls();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { AddProfiles(Profiles.ParseList(await load(timeout.Token))); }
        catch (Exception error) { ImportError(error); }
        finally
        {
            busy = false;
            UpdateControls();
            if (closeRequested) Close();
        }
    }

    private void AddProfiles(IReadOnlyList<Profile> added)
    {
        if (profiles.Count + added.Count > Profiles.MaxProfiles)
            throw new VpnError("Можно сохранить до 100 профилей.");
        LocalData.Save(LocalData.Profiles, profiles.Concat(added).ToArray());
        foreach (var profile in added) profiles.Add(profile);
        Servers.SelectedItem = added[0];
        AddLog("Импортировано профилей: " + added.Count + ".");
        Detail.Text = "Профили сохранены с защитой Windows DPAPI.";
    }

    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (Servers.SelectedItem is not Profile profile) return;
        try
        {
            var remaining = profiles.ToList();
            remaining.Remove(profile);
            LocalData.Save(LocalData.Profiles, remaining.ToArray());
            profiles.Remove(profile);
            if (profiles.Count != 0) Servers.SelectedIndex = 0;
        }
        catch { ReportProblem("Не удалось сохранить список серверов."); }
        UpdateControls();
    }

    private void ImportError(Exception error) => ReportProblem(error switch
    {
        VpnError => error.Message,
        OperationCanceledException => "Импорт остановлен по тайм-ауту.",
        HttpRequestException => "Не удалось получить подписку по HTTPS.",
        _ => "Не удалось прочитать или сохранить профили. Код " + Code(error) + "."
    });

    private void ReportProblem(string message)
    {
        Detail.Text = message;
        AddLog(message);
    }

    private void AddLog(string message)
    {
        if (journal.Count >= 200) journal.Dequeue();
        journal.Enqueue(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " " + message);
        LogBox.Text = string.Join(Environment.NewLine, journal);
        LogBox.ScrollToEnd();
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "pomogator-vpn-diagnostics", Filter = "Текст (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllLines(dialog.FileName, new[] { "ПОМОГАТОР VPN 0.1.0" }.Concat(journal), Encoding.UTF8); }
        catch { ReportProblem("Не удалось сохранить отчёт. Проверьте доступ к выбранной папке."); }
    }

    private static string Code(Exception error) => "0x" + error.HResult.ToString("X8");
}
