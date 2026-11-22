using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pomogator.Vpn;

public partial class MainWindow : Window
{
    internal readonly ObservableCollection<Profile> ProfilesList = new();
    internal readonly ConnectionState Connection = new();
    internal TrayIcon? Tray { get; private set; }
    internal bool StartInTray { get; init; }

    private readonly SystemProxy proxy = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<string> journal = new();
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(30);
    private Xray? core;
    private int checkPort;
    private int? socksPort;
    private Phase? rendered;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? health;
    private DateTimeOffset? lastCheck;
    private long nextHealthCheck;
    private bool busy, storageReady, closeRequested, verifying, measuring;

    public MainWindow()
    {
        InitializeComponent();
        Servers.ItemsSource = ProfilesList;
        Connection.Changed += Render;
        Loaded += Ready;
        Closing += ClosingWindow;
        Closed += (_, _) => Tray?.Dispose();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && Tray != null) Hide(); };
        SourceInitialized += (_, _) => UseDarkTitle();
        timer.Tick += Monitor;
    }

    private void Ready(object sender, RoutedEventArgs e)
    {
        if (Tray != null) return;
        Tray = new TrayIcon(() => Dispatcher.BeginInvoke(ShowFromTray),
            () => Dispatcher.BeginInvoke(async () => await StopFromUi()), () => Dispatcher.BeginInvoke(Close));
        try
        {
            foreach (var profile in LocalData.Read<Profile[]>(LocalData.Profiles) ?? []) ProfilesList.Add(profile);
            storageReady = true;
            if (ProfilesList.Count != 0) Servers.SelectedIndex = 0;
        }
        catch { ReportProblem("Не удалось расшифровать список серверов. Существующий файл сохранён."); }
        if (proxy.HasRecovery && MessageBox.Show(this,
            "Есть запись предыдущего сеанса. Восстановить исходный системный прокси, если текущие параметры всё ещё совпадают с настройками ПОМОГАТОР?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            RestoreProxy(fromDisk: true);
        try { LaunchWithWindows.IsChecked = Startup.Enabled; }
        catch { AddLog("Windows не позволила прочитать настройку автозапуска."); }
        timer.Start();
        Render();
        if (StartInTray) Hide();
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void UseDarkTitle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        int dark = 1;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int option, ref int value, int bytes);

    private void OverviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ServersPanel == null) return;
        var stack = e.NewSize.Width < 720;
        Grid.SetColumn(ServersPanel, stack ? 0 : 1);
        Grid.SetRow(ServersPanel, stack ? 1 : 0);
        Overview.ColumnDefinitions[1].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Hero.Margin = stack ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 14, 0);
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        LatencyText.Text = "Задержка измеряется по TCP до сервера.";
        Render();
    }

    private void ModeChanged(object sender, RoutedEventArgs e) { if (IsLoaded) Render(); }
    private void SettingsClick(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void StartupClick(object sender, RoutedEventArgs e)
    {
        var enable = LaunchWithWindows.IsChecked == true;
        try
        {
            Startup.Enabled = enable;
            AddLog(enable ? "Автозапуск приложения в трее включён." : "Автозапуск приложения выключен.");
        }
        catch
        {
            LaunchWithWindows.IsChecked = !enable;
            ReportProblem("Не удалось изменить автозапуск. Проверьте разрешения Windows.");
        }
    }

    private void Render()
    {
        var phase = Connection.Phase;
        if (rendered != phase)
        {
            var state = StatusPresentation.For(phase);
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(state.Color));
            Status.Text = state.Label;
            Status.Foreground = StatusGlyph.Foreground = brush;
            StatusGlyph.Text = state.Glyph;
            Connect.BorderBrush = Pulse.Stroke = brush;
            Pulse.BeginAnimation(OpacityProperty, null);
            Pulse.Opacity = phase == Phase.Disconnected ? .45 : .85;
            if (phase == Phase.Connecting && SystemParameters.ClientAreaAnimation)
                Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(.25, .9, TimeSpan.FromMilliseconds(850)) {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
                });
            Tray?.Set(phase);
            rendered = phase;
        }

        var connected = phase == Phase.Connected;
        var editable = !busy && !connected && phase != Phase.Disconnecting;
        var selected = Servers.SelectedItem as Profile;
        ActionLabel.Text = phase switch {
            Phase.Connecting => "Отменить", Phase.Connected => "Отключиться",
            Phase.Disconnecting => "Отключение…", Phase.Error => "Повторить попытку", _ => "Подключиться"
        };
        AutomationProperties.SetName(Connect, ActionLabel.Text);
        Connect.IsEnabled = (phase == Phase.Connecting && operation != null) || connected || editable && selected != null;
        Servers.IsEnabled = Modes.IsEnabled = editable;
        SystemMode.IsEnabled = !proxy.HasRecovery;
        if (!SystemMode.IsEnabled && editable && SystemMode.IsChecked == true) LocalMode.IsChecked = true;
        Paste.IsEnabled = Import.IsEnabled = Subscribe.IsEnabled = editable && storageReady;
        Rename.IsEnabled = Remove.IsEnabled = Paste.IsEnabled && selected != null;
        Latency.IsEnabled = selected != null && !measuring && !busy;
        Verify.IsEnabled = connected && !verifying;
        SelectedServer.Text = selected?.DisplayName ?? "Выберите сервер";
        EmptyServers.Visibility = ProfilesList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var mode = phase == Phase.Connected ? Connection.SystemProxy : SystemMode.IsChecked == true;
        var modeText = mode ? "Системный прокси Windows" : "Локальный прокси";
        var protocol = selected == null ? "" : "VLESS · " + Transport(selected.Transport) +
            (selected.Security == "none" ? " (тестовый)" : " · " + selected.Security.ToUpperInvariant()) + "  |  ";
        ConnectionMeta.Text = protocol + modeText + (Connection.ConnectedAt is { } active
            ? "  |  " + Uptime(DateTimeOffset.Now - active) : "");
        CoreCheck.Text = CheckText("Xray", Check.Xray);
        ProxyCheck.Text = CheckText("Локальный прокси", Check.Proxy);
        TrafficCheck.Text = CheckText("HTTPS через VLESS", Check.Https);
        SystemCheck.Text = "Системный прокси: " + (Connection.SystemProxy ? "включён" : proxy.Active ? "требует проверки" : "выключен");
        CheckedAt.Text = lastCheck is { } stamp ? "Последняя проверка: " + stamp.ToLocalTime().ToString("HH:mm:ss") : "Проверок ещё не было.";
    }

    private static string Uptime(TimeSpan span) => $"{(long)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private static string Transport(string name) => name switch {
        "raw" => "TCP", "ws" => "WebSocket", "grpc" => "gRPC", "xhttp" => "XHTTP", "httpupgrade" => "HTTPUpgrade", _ => name
    };

    private string CheckText(string label, Check check) => label + ": " + (Connection.Result(check) switch {
        CheckResult.Passed => "✓ пройдено", CheckResult.Failed => "! сбой", _ => "— ожидает"
    });

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (Connection.Phase == Phase.Connecting)
        {
            Connection.BeginStop();
            operation?.Cancel();
            return;
        }
        if (Connection.Phase == Phase.Connected) { await StopFromUi(); return; }
        if (busy || Servers.SelectedItem is not Profile profile) return;

        var systemMode = SystemMode.IsChecked == true;
        if (systemMode && MessageBox.Show(this,
            "Включить системный прокси после подтверждения VPN? Исходные параметры WinINet будут сохранены для восстановления.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        using var source = new CancellationTokenSource();
        operation = source;
        busy = true;
        lastCheck = null;
        Connection.Begin();
        Check? stage = null;
        try
        {
            Detail.Text = "Проверка TCP-порта VPN-сервера…";
            var latency = await Xray.CheckServerAsync(profile, source.Token);
            LatencyText.Text = $"Задержка TCP до сервера: {Math.Round(latency.TotalMilliseconds)} мс.";
            AddLog("TCP-порт VPN-сервера доступен.");

            stage = Check.Xray;
            var path = Path.Combine(AppContext.BaseDirectory, "xray.exe");
            if (!File.Exists(path)) throw new VpnError("В комплекте отсутствует xray.exe. Распакуйте архив полностью.");
            var port = Xray.FreePort();
            checkPort = profile.Full == null ? port : DistinctPort(port);
            socksPort = profile.Full?.SocksTag != null ? DistinctPort(port, checkPort) : null;
            var config = XrayConfig.Create(profile, port, checkPort, socksPort);
            Detail.Text = "Запуск Xray…";
            core = await Xray.StartAsync(path, config, port, source.Token,
                WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback, () =>
                {
                    Connection.Record(Check.Xray, CheckResult.Passed);
                    AddLog("Процесс Xray запущен.");
                    stage = Check.Proxy;
                    Detail.Text = "Проверка входов локального прокси…";
                });
            if (checkPort != port && !await core.IsProxyReadyAsync(checkPort, source.Token) ||
                socksPort is { } socks && !await core.IsProxyReadyAsync(socks, source.Token))
                throw new VpnError("Xray не открыл один из локальных портов профиля.");
            Connection.Record(Check.Proxy, CheckResult.Passed);
            AddLog("Локальные порты принадлежат Xray и принимают подключения.");

            stage = Check.Https;
            Detail.Text = "Контрольный HTTPS-запрос через выбранный VLESS…";
            await Xray.VerifyAsync(checkPort, source.Token);
            lastCheck = DateTimeOffset.Now;
            Connection.Record(Check.Https, CheckResult.Passed);
            AddLog("HTTPS через выбранный VLESS вернул HTTP 204.");

            stage = Check.Xray;
            source.Token.ThrowIfCancellationRequested();
            if (!core.Running) throw new VpnError("Xray завершился после проверки.");
            stage = null;
            if (systemMode)
            {
                proxy.Enable(port);
                AddLog("Системный прокси включён для текущего пользователя.");
            }
            health = new CancellationTokenSource();
            nextHealthCheck = Environment.TickCount64 + (long)HealthInterval.TotalMilliseconds;
            Connection.Confirm(lastCheck.Value, systemMode);
            Detail.Text = $"HTTP 127.0.0.1:{port}" + (socksPort is { } sock ? $"   ·   SOCKS 127.0.0.1:{sock}" : "");
        }
        catch (Exception) when (source.IsCancellationRequested)
        {
            Stop(false);
            AddLog("Подключение отменено.");
        }
        catch (Exception error)
        {
            if (stage is { } failed) Connection.Record(failed, CheckResult.Failed);
            lastCheck = DateTimeOffset.Now;
            Stop(true, Explain(error));
        }
        finally
        {
            operation = null;
            busy = false;
            Render();
            if (closeRequested) Close();
        }
    }

    private static int DistinctPort(params int[] reserved)
    {
        for (var i = 0; i < 10; i++)
        {
            var port = Xray.FreePort();
            if (!reserved.Contains(port)) return port;
        }
        throw new VpnError("Не удалось выделить отдельные локальные порты. Повторите подключение.");
    }

    private async Task StopFromUi()
    {
        if (Connection.Phase != Phase.Connected) return;
        Connection.BeginStop();
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (Connection.Phase == Phase.Disconnecting) Stop(false);
    }

    private void Stop(bool failed, string? reason = null)
    {
        Connection.BeginStop();
        health?.Cancel();
        health?.Dispose();
        health = null;
        var restored = RestoreProxy();
        try { core?.Dispose(); }
        catch (Exception error)
        {
            failed = true;
            AddLog("Ошибка остановки ядра. Код " + Code(error) + ".");
        }
        core = null;
        var detail = reason ?? (failed ? "Сеанс завершён с ошибкой." : "Сеанс завершён.");
        if (!restored) detail += " Восстановление системного прокси ожидает повторной проверки.";
        Connection.End(failed || !restored);
        Detail.Text = detail;
        AddLog(detail);
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

    private async void Monitor(object? sender, EventArgs e)
    {
        if (Connection.Phase != Phase.Connected || core == null) return;
        if (!core.Running)
        {
            Connection.Record(Check.Xray, CheckResult.Failed);
            lastCheck = DateTimeOffset.Now;
            Stop(true, "Процесс Xray завершился. Повторите подключение.");
            return;
        }
        try
        {
            if (proxy.ReleaseIfChanged())
            {
                Connection.ReleaseSystemProxy();
                ReportProblem("Системный прокси изменился извне. Ядро работает в локальном режиме.");
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Stop(true, "Не удалось проверить системный прокси. Код " + Code(error) + ".");
            return;
        }
        Render();
        if (Environment.TickCount64 >= nextHealthCheck) await CheckRunningConnection(false);
    }

    private async void VerifyClick(object sender, RoutedEventArgs e)
    {
        DiagnosticsPanel.IsExpanded = true;
        await CheckRunningConnection(true);
    }

    private async Task CheckRunningConnection(bool manual)
    {
        if (verifying || core is not { } process || health == null || Connection.Phase != Phase.Connected) return;
        var token = health.Token;
        var port = checkPort;
        var socks = socksPort;
        var failedStage = Check.Xray;
        verifying = true;
        Verify.Content = "Проверяется…";
        Render();
        try
        {
            if (!process.Running) throw new VpnError("Процесс Xray завершился.");
            failedStage = Check.Proxy;
            if (!await process.IsProxyReadyAsync(process.Port, token) ||
                port != process.Port && !await process.IsProxyReadyAsync(port, token) ||
                socks is { } optional && !await process.IsProxyReadyAsync(optional, token))
                throw new VpnError("Локальный прокси перестал отвечать.");
            failedStage = Check.Https;
            await Xray.VerifyAsync(port, token);
            if (token.IsCancellationRequested || core != process) return;
            lastCheck = DateTimeOffset.Now;
            nextHealthCheck = Environment.TickCount64 + (long)HealthInterval.TotalMilliseconds;
            if (manual) AddLog("Повторная проверка HTTPS через VLESS пройдена.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (token.IsCancellationRequested || core != process) return;
            Connection.Record(failedStage, CheckResult.Failed);
            lastCheck = DateTimeOffset.Now;
            Stop(true, Explain(error) + " Повторите подключение.");
        }
        finally
        {
            verifying = false;
            Verify.Content = "Проверить соединение";
            if (!closeRequested) Render();
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
        closeRequested = true;
        timer.Stop();
        if (core != null || proxy.Active) Stop(false);
    }

    private void PasteClick(object sender, RoutedEventArgs e)
    {
        try { ImportText(Clipboard.GetText()); }
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
        var field = new PasswordBox { Margin = new Thickness(0, 8, 0, 12) };
        var button = new Button { Content = "Загрузить", IsDefault = true };
        var dialog = Dialog("Импорт подписки", "Вставьте HTTPS-адрес подписки.", field, button);
        button.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => field.Focus();
        if (dialog.ShowDialog() != true) return;
        var address = field.Password.Trim();
        field.Clear();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !Subscriptions.IsHttps(uri))
        {
            ReportProblem("Нужен URL HTTPS без имени пользователя, пароля и фрагмента.");
            return;
        }
        await ImportAsync(async token =>
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                UseCookies = false, UseDefaultCredentials = false, MaxResponseHeadersLength = 32 };
            using var http = new HttpClient(handler);
            return await Subscriptions.DownloadAsync(http, uri, token);
        });
    }

    private async Task ImportAsync(Func<CancellationToken, Task<string>> load)
    {
        busy = true;
        Render();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var content = await load(timeout.Token);
            if (!closeRequested) ImportText(content);
        }
        catch (Exception error) { ImportError(error); }
        finally
        {
            busy = false;
            Render();
            if (closeRequested) Close();
        }
    }

    private void ImportText(string text)
    {
        var json = Profiles.IsXrayJson(text);
        var keepRouting = false;
        if (json)
        {
            var full = new Button { Content = "Полный профиль", Margin = new Thickness(0, 9, 0, 6) };
            var simple = new Button { Content = "Упрощённый профиль" };
            var dialog = Dialog("Импорт JSON Xray",
                "Полный профиль сохраняет совместимые DNS, маршруты и исходящие подключения. " +
                "Упрощённый переносит только выбранный VLESS-сервер. Индивидуальные правила применяться не будут.",
                full, simple);
            full.Click += (_, _) => { keepRouting = true; dialog.DialogResult = true; };
            simple.Click += (_, _) => dialog.DialogResult = true;
            if (dialog.ShowDialog() != true) return;
        }
        var added = Profiles.ParseList(text, keepRouting);
        if (ProfilesList.Count + added.Count > Profiles.MaxProfiles) throw new VpnError("Можно сохранить до 100 профилей.");
        LocalData.Save(LocalData.Profiles, ProfilesList.Concat(added).ToArray());
        foreach (var profile in added) ProfilesList.Add(profile);
        Servers.SelectedItem = added[0];
        AddLog("Импортировано профилей: " + added.Count + (json ? keepRouting ? " (полный JSON)." : " (упрощённый JSON)." : "."));
        Detail.Text = json && !keepRouting ? "Сохранён VLESS-сервер. DNS и правила из файла опущены." : "Профили сохранены с защитой DPAPI.";
        Render();
    }

    private Window Dialog(string title, string description, params Control[] controls)
    {
        var panel = new StackPanel { Margin = new Thickness(17) };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        foreach (var control in controls) panel.Children.Add(control);
        return new Window { Owner = this, Title = title, Width = 465, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, Content = panel };
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
        if (Servers.SelectedItem is not Profile profile) return;
        var field = new TextBox { Text = profile.DisplayName, MaxLength = 100, Margin = new Thickness(0, 9, 0, 9) };
        var accept = new Button { Content = "Сохранить", IsDefault = true };
        var dialog = Dialog("Переименование", "Введите новое имя сервера.", field, accept);
        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        if (dialog.ShowDialog() != true) return;
        var name = field.Text.Trim();
        if (Profiles.SafeName(name, "") == "" || name.Any(char.IsControl))
        {
            ReportProblem("Введите короткое название без ключей и ссылок.");
            return;
        }
        var index = Servers.SelectedIndex;
        var renamed = profile with { Name = name };
        try
        {
            var saved = ProfilesList.ToArray();
            saved[index] = renamed;
            LocalData.Save(LocalData.Profiles, saved);
            ProfilesList[index] = renamed;
            Servers.SelectedIndex = index;
            AddLog("Имя сервера сохранено.");
        }
        catch { ReportProblem("Не удалось сохранить имя сервера."); }
    }

    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (Servers.SelectedItem is not Profile profile) return;
        try
        {
            var remaining = ProfilesList.ToList();
            remaining.Remove(profile);
            LocalData.Save(LocalData.Profiles, remaining.ToArray());
            ProfilesList.Remove(profile);
            if (ProfilesList.Count != 0) Servers.SelectedIndex = 0;
            AddLog("Профиль удалён.");
        }
        catch { ReportProblem("Не удалось сохранить список серверов."); }
        Render();
    }

    private async void LatencyClick(object sender, RoutedEventArgs e)
    {
        if (measuring || Servers.SelectedItem is not Profile profile) return;
        measuring = true;
        LatencyText.Text = "Измерение времени TCP-соединения…";
        Render();
        try
        {
            var latency = await Xray.CheckServerAsync(profile, CancellationToken.None);
            if (ReferenceEquals(Servers.SelectedItem, profile))
                LatencyText.Text = $"Задержка TCP до сервера: {Math.Round(latency.TotalMilliseconds)} мс.";
        }
        catch (Exception error)
        {
            if (ReferenceEquals(Servers.SelectedItem, profile)) LatencyText.Text = Explain(error);
        }
        finally
        {
            measuring = false;
            if (!closeRequested) Render();
        }
    }

    private void ImportError(Exception error) => ReportProblem(error switch {
        VpnError => error.Message, OperationCanceledException => "Импорт остановлен по тайм-ауту.",
        HttpRequestException => "Не удалось получить подписку по HTTPS. Проверьте сеть и сертификат поставщика.",
        _ => "Не удалось прочитать или сохранить профили. Код " + Code(error) + "."
    });

    private void ReportProblem(string message)
    {
        Detail.Text = message;
        AddLog(message);
    }

    internal void AddLog(string message)
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
        try
        {
            var header = new[] { "ПОМОГАТОР VPN 0.2.0", Status.Text, CoreCheck.Text, ProxyCheck.Text, TrafficCheck.Text,
                SystemCheck.Text, CheckedAt.Text, "" };
            File.WriteAllLines(dialog.FileName, header.Concat(journal), Encoding.UTF8);
        }
        catch { ReportProblem("Не удалось сохранить отчёт. Проверьте доступ к выбранной папке."); }
    }

    private static string Explain(Exception error) => error is VpnError ? error.Message
        : "Windows или ядро отклонили операцию. Код " + Code(error) + ".";
    private static string Code(Exception error) => "0x" + error.HResult.ToString("X8");
}
