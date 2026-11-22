using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private HealthPolicy healthPolicy = new();
    private TunSession? tun;
    private TunObservation? tunObservation;
    private TunChecks? tunChecks;
    private string? tunStage, tunEngineNote, tunEnd;
    private long nextPeerCheck;
    private string? peerHost;
    private bool startingTun;
    private string tunStatus = "TUN: выключен.";
    private string ipStatus = "Внешний IP: ожидает проверки.";
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

    private async void Ready(object sender, RoutedEventArgs e)
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
        if (File.Exists(LocalData.TunRecovery))
        {
            try
            {
                var recovery = LocalData.Read<TunRecovery>(LocalData.TunRecovery)
                    ?? throw new VpnError("Запись восстановления TUN повреждена.");
                if (TunWindows.Clean(recovery))
                {
                    File.Delete(LocalData.TunRecovery);
                    AddLog("После предыдущего сеанса интерфейс TUN отсутствует.");
                }
                else if (MessageBox.Show(this,
                    "Остался интерфейс предыдущего сеанса TUN. Проверить его и удалить только при совпадении с записью ПОМОГАТОР?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    busy = true;
                    Render();
                    using var previous = new TunSession(recovery);
                    await previous.RestoreAsync(CancellationToken.None);
                    AddLog("Предыдущий интерфейс TUN удалён.");
                }
            }
            catch (Exception error) { ReportProblem(Explain(error)); }
            finally { busy = false; }
        }
        try { LaunchWithWindows.IsChecked = Startup.Enabled; }
        catch { AddLog("Windows не позволила прочитать настройку автозапуска."); }
        timer.Start();
        Render();
        if (closeRequested) { Close(); return; }
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
        if (Connection.Phase == Phase.Disconnected && Servers.SelectedItem != null)
            Detail.Text = "Нажмите круглую кнопку, чтобы подключиться.";
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

        var connected = Connection.Active;
        var editable = !busy && !connected && phase != Phase.Disconnecting;
        var selected = Servers.SelectedItem as Profile;
        ActionLabel.Text = phase switch {
            Phase.Connecting => "Отменить", Phase.Connected or Phase.Unstable => "Отключиться",
            Phase.Disconnecting => "Отключение…", Phase.Error => "Повторить попытку", _ => "Подключиться"
        };
        AutomationProperties.SetName(Connect, ActionLabel.Text);
        Connect.IsEnabled = (phase == Phase.Connecting && operation != null) || connected || editable && selected != null;
        Servers.IsEnabled = Modes.IsEnabled = editable;
        SystemMode.IsEnabled = !proxy.HasRecovery;
        TunMode.IsEnabled = TunWindows.SupportsWindows && !File.Exists(LocalData.TunRecovery) && !proxy.HasRecovery;
        ModeHint.Text = connected || phase is Phase.Connecting or Phase.Disconnecting
            ? "Для смены режима сначала отключите VPN."
            : File.Exists(LocalData.TunRecovery) ? "Ожидается восстановление предыдущего TUN. Перезапустите приложение для проверки."
            : !TunWindows.SupportsWindows ? "Для TUN нужна Windows 10 22H2 x64 или новее." : "";
        if (!TunMode.IsEnabled && editable && TunMode.IsChecked == true) LocalMode.IsChecked = true;
        if (!SystemMode.IsEnabled && editable && SystemMode.IsChecked == true) LocalMode.IsChecked = true;
        Paste.IsEnabled = Import.IsEnabled = Subscribe.IsEnabled = editable && storageReady;
        Rename.IsEnabled = Remove.IsEnabled = Paste.IsEnabled && selected != null;
        Latency.IsEnabled = selected != null && !measuring && !busy;
        Verify.IsEnabled = connected && !verifying;
        SelectedServer.Text = selected?.DisplayName ?? "Выберите сервер";
        EmptyServers.Visibility = ProfilesList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var mode = connected ? Connection.SystemProxy : SystemMode.IsChecked == true;
        var modeText = tun != null || startingTun || !connected && TunMode.IsChecked == true ? "VPN-туннель TUN" :
            mode ? "Системный прокси Windows" : "Локальный прокси";
        var protocol = selected == null ? "" : "VLESS · " + Transport(selected.Transport) +
            (selected.Security == "none" ? " (тестовый)" : " · " + selected.Security.ToUpperInvariant()) + "  |  ";
        ConnectionMeta.Text = protocol + modeText + (Connection.ConnectedAt is { } active
            ? "  |  " + Uptime(DateTimeOffset.Now - active) : "");
        CoreCheck.Text = CheckText("Xray", Check.Xray);
        ProxyCheck.Text = CheckText("Локальный прокси", Check.Proxy);
        TrafficCheck.Text = CheckText("HTTPS через VLESS", Check.Https);
        SystemCheck.Text = "Системный прокси: " + (Connection.SystemProxy ? "включён" : proxy.Active ? "требует проверки" : "выключен");
        TunCheck.Text = tunStatus;
        ExternalCheck.Text = ipStatus;
        CheckedAt.Text = (lastCheck ?? Connection.ConnectedAt) is { } stamp ? "Последняя проверка: " + stamp.ToLocalTime().ToString("HH:mm:ss") : "Проверок ещё не было.";
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
        if (Connection.Active) { await StopFromUi(); return; }
        if (busy || Servers.SelectedItem is not Profile profile) return;

        var systemMode = SystemMode.IsChecked == true;
        if (systemMode && MessageBox.Show(this,
            "Включить системный прокси после подтверждения VPN? Исходные параметры WinINet будут сохранены для восстановления.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var tunMode = TunMode.IsChecked == true;
        if (tunMode && MessageBox.Show(this,
            "Запустить TUN с правами администратора? Wintun создаст временный интерфейс, маршруты IPv4 и DNS на этом интерфейсе. " +
            "IPv6 будет блокироваться до завершения. Для TUN весь публичный трафик идёт через выбранный VLESS. " +
            "Прямые правила полного профиля применяются в режимах прокси. Во время сеанса могут быть недоступны DNS-запросы через ICS.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        using var source = new CancellationTokenSource();
        operation = source;
        busy = true;
        lastCheck = null;
        startingTun = tunMode;
        tunObservation = null;
        tunChecks = null;
        tunStage = tunMode ? "подготовка и поиск рабочего маршрута" : null;
        tunEngineNote = tunEnd = null;
        ipStatus = "Внешний IP: ожидает проверки.";
        tunStatus = tunMode ? "TUN: подготовка." : "TUN: выключен.";
        healthPolicy = new HealthPolicy();
        Connection.Begin();
        Check? stage = null;
        try
        {
            Detail.Text = "Проверка TCP-порта VPN-сервера…";
            IPAddress? peer = null;
            TunRecovery? recovery = null;
            string? bridgePassword = null;
            if (tunMode)
            {
                if (!TunWindows.SupportsWindows)
                    throw new VpnError("Для этого режима нужна Windows 10 22H2 x64 или новее.");
                if (TunWindows.OtherVpn() || new WinInet().Read().InUse)
                    throw new VpnError("Обнаружен другой VPN, UBoost или активный системный прокси. Выберите локальный прокси или завершите другой клиент самостоятельно.");
                using var dnsDeadline = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                dnsDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(profile.Host, AddressFamily.InterNetwork, dnsDeadline.Token);
                    peer = addresses.FirstOrDefault(address => !IPAddress.IsLoopback(address) &&
                        address.ToString() != TunConfig.Resolver && address.ToString() != TunConfig.InternetProbe);
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException && !source.IsCancellationRequested)
                { throw new VpnError("Не удалось получить IPv4-адрес VPN-сервера через текущий DNS."); }
                if (peer == null) throw new VpnError("У VPN-сервера нет пригодного IPv4-адреса для TUN.");
                recovery = new TunRecovery(Guid.NewGuid(), peer.ToString(), TunWindows.Capture(peer),
                    TunConfig.Routes(peer));
                bridgePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            }
            var latency = await Xray.CheckServerAsync(peer == null ? profile : profile with { Host = peer.ToString() }, source.Token);
            LatencyText.Text = $"Задержка TCP до сервера: {Math.Round(latency.TotalMilliseconds)} мс.";
            AddLog("TCP-порт VPN-сервера доступен.");

            stage = Check.Xray;
            var path = Path.Combine(AppContext.BaseDirectory, "xray.exe");
            if (!File.Exists(path)) throw new VpnError("В комплекте отсутствует xray.exe. Распакуйте архив полностью.");
            var port = Xray.FreePort();
            checkPort = tunMode || profile.Full == null ? port : DistinctPort(port);
            socksPort = tunMode || profile.Full?.SocksTag != null ? DistinctPort(port, checkPort) : null;
            var config = tunMode ? TunConfig.Bridge(profile, peer!, port, socksPort!.Value, bridgePassword!) :
                XrayConfig.Create(profile, port, checkPort, socksPort);
            if (tunMode) tunStage = "запуск Xray для выбранного VLESS";
            Detail.Text = "Запуск Xray…";
            core = await Xray.StartAsync(path, config, port, source.Token,
                WindowsLifetime.Attach, WindowsLifetime.OwnsLoopback, () =>
                {
                    Connection.Record(Check.Xray, CheckResult.Passed);
                    AddLog("Процесс Xray запущен.");
                    stage = Check.Proxy;
                    if (tunMode) tunStage = "проверка локальных портов Xray";
                    Detail.Text = "Проверка входов локального прокси…";
                }, startupExit: tunMode ? code => tunEngineNote = BridgeExit(code) : null);
            if (checkPort != port && !await core.IsProxyReadyAsync(checkPort, source.Token) ||
                socksPort is { } socks && !await core.IsProxyReadyAsync(socks, source.Token))
                throw new VpnError("Xray не открыл один из локальных портов профиля.");
            Connection.Record(Check.Proxy, CheckResult.Passed);
            AddLog("Локальные порты принадлежат Xray и принимают подключения.");

            stage = null;
            TimeSpan result;
            if (recovery != null)
            {
                tunStage = "запуск Wintun и подтверждение параметров Windows";
                Detail.Text = "Ожидание интерфейса, IPv4-адреса, маршрутов и DNS Wintun…";
                tun = new TunSession(recovery);
                try { await tun.StartAsync(socksPort!.Value, bridgePassword!, source.Token); }
                finally
                {
                    tunEngineNote = tun.EngineNote;
                    tunObservation = tun.Observation ?? new TunObservation(TunConfig.AdapterId(recovery.Id), TunConfig.Adapter(recovery.Id),
                        Uplink: recovery.Before.Uplink, ExpectedDns: recovery.InstalledDns, ErrorCode: "TUN_NO_SNAPSHOT");
                }
                tunStatus = "TUN: параметры виртуального интерфейса подтверждены.";
                AddLog(tunStatus);
                tunStage = "проверка DNS и HTTPS";
                Detail.Text = "Проверяем DNS Windows, DNS через Wintun и HTTPS через Wintun…";
                tunChecks = await TunConnectivity.Run(recovery, source.Token);
                Connection.Record(Check.Https, tunChecks.Https.Success ? CheckResult.Passed : CheckResult.Failed);
                foreach (var line in tunChecks.Lines()) AddLog(line);
                if (!tunChecks.Success)
                {
                    tunStage = tunChecks.FailedStage;
                    throw new VpnError(tunChecks.Failure);
                }
                tunStage = "повторное подтверждение интерфейса и маршрутов после проверки трафика";
                var finalObservation = TunWindows.Observe(recovery);
                if (finalObservation.Failure is { } readinessFailure)
                {
                    tunObservation = finalObservation with { WaitedMilliseconds = tunObservation?.WaitedMilliseconds ?? 0 };
                    throw new VpnError(readinessFailure);
                }
                if (!TunWindows.Same(recovery.Before, TunWindows.Capture(peer!)))
                    throw new VpnError("Состояние исходного интерфейса изменилось во время проверки TUN.");
                tunStatus = "TUN: интерфейс, маршруты, системный DNS и HTTPS через Wintun подтверждены. IPv6 блокируется.";
                peerHost = profile.Host;
                nextPeerCheck = Environment.TickCount64 + 120000;
                result = TimeSpan.FromMilliseconds(tunChecks.Https.Milliseconds);
            }
            else
            {
                stage = Check.Https;
                Detail.Text = "Контрольные HTTPS-запросы через выбранный VLESS…";
                using var http = NetworkProbeClient();
                result = await InternetHealth.ProbeAsync(http, source.Token) ??
                    throw new VpnError("Контрольные адреса не ответили через выбранный VLESS. Доступ не подтверждён.");
            }
            lastCheck = DateTimeOffset.Now;
            Connection.Record(Check.Https, CheckResult.Passed);
            AddLog($"HTTPS через выбранный VLESS подтверждён за {Math.Round(result.TotalMilliseconds)} мс.");

            stage = Check.Xray;
            source.Token.ThrowIfCancellationRequested();
            if (!core.Running)
            {
                if (tunMode) tunEngineNote = BridgeExit(core.ExitCode);
                throw new VpnError("Xray завершился после проверки.");
            }
            if (tun is { Running: false } closedTun)
            {
                tunEngineNote = await closedTun.ReadEngineStop();
                throw new VpnError("Помощник TUN завершился после проверки трафика.");
            }
            if (tunMode) tunStage = "проверки запуска завершены";
            stage = null;
            if (systemMode)
            {
                proxy.Enable(port);
                AddLog("Системный прокси включён для текущего пользователя.");
            }
            health = new CancellationTokenSource();
            ScheduleHealth(healthPolicy.Observe(true));
            Connection.Confirm(lastCheck.Value, systemMode);
            Detail.Text = tunMode ? "IPv4 идёт через туннель. IPv6 в этом сеансе блокируется." :
                $"HTTP 127.0.0.1:{port}" + (socksPort is { } sock ? $"   ·   SOCKS 127.0.0.1:{sock}" : "");
            _ = CheckExternalIp(core, health.Token);
        }
        catch (Exception) when (source.IsCancellationRequested)
        {
            if (tunMode) tunEnd = "подключение отменено пользователем";
            Stop(false);
            AddLog("Подключение отменено.");
        }
        catch (Exception error)
        {
            if (((error as VpnError)?.FailedCheck ?? stage) is { } failed) Connection.Record(failed, CheckResult.Failed);
            lastCheck = DateTimeOffset.Now;
            if (tunMode)
            {
                if (tun is { } session && !session.Running)
                    tunEngineNote = await session.ReadEngineStop() ?? tunEngineNote;
                tunEnd = Explain(error);
            }
            Stop(true, Explain(error));
        }
        finally
        {
            operation = null;
            startingTun = false;
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
        if (!Connection.Active) return;
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
        if (tun != null && tunEnd == null)
            tunEnd = reason ?? "штатная остановка по запросу пользователя";
        var routeRestored = FinishTun();
        try { core?.Dispose(); }
        catch (Exception error)
        {
            failed = true;
            AddLog("Ошибка остановки ядра. Код " + Code(error) + ".");
        }
        core = null;
        var detail = reason ?? (failed ? "Сеанс завершён с ошибкой." : "Сеанс завершён.");
        if (!restored) detail += " Восстановление системного прокси ожидает повторной проверки.";
        if (!routeRestored) detail += " Восстановление TUN ожидает повторной проверки.";
        Connection.End(failed || !restored || !routeRestored);
        Detail.Text = detail;
        AddLog(detail);
    }

    private bool FinishTun()
    {
        if (tun is not { } active) return true;
        tun = null;
        var record = active.Record;
        try
        {
            active.Dispose();
            if (!TunWindows.Clean(record))
            {
                tunStatus = "TUN: интерфейс остался. Запись восстановления сохранена.";
                AddLog(tunStatus);
                return false;
            }
            File.Delete(LocalData.TunRecovery);
            try
            {
                var current = TunWindows.Capture(IPAddress.Parse(record.Peer));
                tunStatus = TunWindows.MatchesBaseline(record.Before, current)
                    ? "TUN: интерфейс и собственные маршруты удалены. Исходные маршруты и DNS подтверждены."
                    : "TUN: интерфейс удалён. Состояние рабочей сети отличается от исходного.";
            }
            catch { tunStatus = "TUN: интерфейс удалён. Проверить состояние текущего подключения не удалось."; }
            AddLog(tunStatus);
            return true;
        }
        catch (Exception error)
        {
            tunStatus = "Проверка восстановления TUN не завершена. Код " + Code(error) + ".";
            AddLog(tunStatus);
            return false;
        }
    }

    private HttpClient NetworkProbeClient() => tun is { } active ? TunWindows.Http(active.Record.Id) : Xray.ThroughProxy(checkPort);

    private void ScheduleHealth(TimeSpan? delay) =>
        nextHealthCheck = delay is { } interval ? Environment.TickCount64 + (long)interval.TotalMilliseconds : long.MaxValue;

    private async Task CheckExternalIp(Xray process, CancellationToken token)
    {
        try
        {
            var tunneling = tun != null;
            using var routed = NetworkProbeClient();
            using var proxied = tunneling ? Xray.ThroughProxy(checkPort) : null;
            var routedQuery = InternetHealth.ExternalIpAsync(routed, token);
            var comparable = proxied != null ? await InternetHealth.ExternalIpAsync(proxied, token) : null;
            var ip = await routedQuery;
            if (token.IsCancellationRequested || core != process) return;
            ipStatus = ip == null ? "Внешний IP: сервис проверки недоступен." :
                "Внешний IP: " + ip + (comparable == null ? "" : comparable == ip
                    ? " · совпадает с проверкой через локальный VLESS."
                    : " · результат через локальный VLESS отличается.");
            Render();
        }
        catch (Exception) { } // Optional; never leak request details or change the session state here.
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
        if (!Connection.Active || core == null) return;
        if (!core.Running)
        {
            if (tun != null)
            {
                tunStage = "контроль работы Xray";
                tunEngineNote = BridgeExit(core.ExitCode);
            }
            Connection.Record(Check.Xray, CheckResult.Failed);
            lastCheck = DateTimeOffset.Now;
            Stop(true, "Процесс Xray завершился. Повторите подключение.");
            return;
        }
        try
        {
            if (tun is { } active)
            {
                var found = TunWindows.Observe(active.Record);
                if (!active.Running || !found.Ready ||
                    !TunWindows.Same(active.Record.Before, TunWindows.Capture(IPAddress.Parse(active.Record.Peer))))
                {
                    tunObservation = found with { WaitedMilliseconds = tunObservation?.WaitedMilliseconds ?? 0 };
                    var helperStopped = !active.Running;
                    tunStage = helperStopped ? "контроль работы Xray TUN" : "контроль интерфейса, DNS и маршрутов";
                    if (helperStopped)
                    {
                        var reason = await active.ReadEngineStop();
                        if (tun != active) return;
                        tunEngineNote = reason;
                    }
                    Stop(true, (helperStopped ? "Помощник TUN завершился." :
                        found.Failure ?? "Состояние исходного интерфейса изменилось.") + " Повторите подключение.");
                    return;
                }
            }
            if (proxy.ReleaseIfChanged())
            {
                Connection.ReleaseSystemProxy();
                ReportProblem("Системный прокси изменился извне. Ядро работает в локальном режиме.");
            }
        }
        catch (Exception error)
        {
            Stop(true, "Не удалось проверить сетевые параметры. Код " + Code(error) + ".");
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
        if (verifying || core is not { } process || health == null || !Connection.Active) return;
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
            if (tun is { } active && Environment.TickCount64 >= nextPeerCheck)
            {
                nextPeerCheck = Environment.TickCount64 + 120000;
                using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
                lookup.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(peerHost!, AddressFamily.InterNetwork, lookup.Token);
                    if (addresses.Length != 0 && !addresses.Select(address => address.ToString()).Contains(active.Record.Peer))
                    {
                        Stop(true, "IPv4-адрес сервера изменился. Подключитесь снова для безопасного обновления маршрута.");
                        return;
                    }
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException)
                { }
            }
            failedStage = Check.Https;
            if (token.IsCancellationRequested || core != process) return;
            if (manual && healthPolicy.ConsecutiveFailures == 3) healthPolicy = new HealthPolicy();
            TimeSpan? result;
            bool? tunHttps = null;
            string? tunFailure = null;
            if (tun is { } session)
            {
                var latest = await TunConnectivity.Run(session.Record, token);
                if (token.IsCancellationRequested || core != process) return;
                tunChecks = latest;
                tunHttps = latest.Https.Success;
                result = latest.Success ? TimeSpan.FromMilliseconds(latest.Https.Milliseconds) : null;
                if (!latest.Success) tunFailure = latest.Failure;
                if (manual || !latest.Success)
                    foreach (var line in latest.Lines()) AddLog(line);
            }
            else
            {
                using var http = NetworkProbeClient();
                result = await InternetHealth.ProbeAsync(http, token);
            }
            if (token.IsCancellationRequested || core != process) return;
            lastCheck = DateTimeOffset.Now;
            var next = healthPolicy.Observe(result.HasValue);
            var wasUnstable = Connection.Phase == Phase.Unstable;
            Connection.ReportHealth(result.HasValue, tunHttps);
            if (result is { } latency)
            {
                if (manual || wasUnstable) AddLog($"HTTPS через VLESS снова подтверждён за {Math.Round(latency.TotalMilliseconds)} мс.");
                if (manual || wasUnstable) _ = CheckExternalIp(process, token);
                if (wasUnstable) Detail.Text = tun != null ? "Доступ через туннель снова подтверждён." :
                    $"HTTP 127.0.0.1:{process.Port}" + (socksPort is { } optionalSocks ? $"   ·   SOCKS 127.0.0.1:{optionalSocks}" : "");
            }
            else
            {
                var failure = tunFailure ?? "Контрольные адреса недоступны.";
                Detail.Text = next == null ? failure + " Сеанс активен. Проверьте соединение вручную." :
                    failure + $" Повтор через {next.Value.TotalSeconds:0} с.";
                AddLog(Detail.Text);
            }
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
        if (core != null || proxy.Active || tun != null) Stop(false);
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
            var header = new[] { "ПОМОГАТОР VPN 0.3.1", Status.Text, CoreCheck.Text, ProxyCheck.Text, TrafficCheck.Text,
                SystemCheck.Text, TunCheck.Text, CheckedAt.Text, "" };
            var observations = tunObservation?.Lines() ?? Enumerable.Empty<string>();
            var probes = tunChecks?.Lines() ?? Enumerable.Empty<string>();
            var stages = new[] { tunStage == null ? null : "Этап TUN: " + tunStage,
                tunEnd == null ? null : "Остановка TUN: " + tunEnd, tunEngineNote }.OfType<string>();
            File.WriteAllLines(dialog.FileName, header.Concat(stages).Concat(observations).Concat(probes).Append("").Concat(journal), Encoding.UTF8);
        }
        catch { ReportProblem("Не удалось сохранить отчёт. Проверьте доступ к выбранной папке."); }
    }

    private static string BridgeExit(int? exitCode) => exitCode is { } code ?
        $"Xray для VLESS завершился с кодом {code}. Причина завершения ядром не подтверждена." :
        "Xray для VLESS завершился. Код выхода недоступен.";

    private static string Explain(Exception error) => error is VpnError ? error.Message
        : "Windows или ядро отклонили операцию. Код " + Code(error) + ".";
    private static string Code(Exception error) => "0x" + error.HResult.ToString("X8");
}
