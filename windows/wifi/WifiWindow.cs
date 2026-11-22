using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PomogatorWifi
{
    internal sealed class WifiWindow : GlassForm
    {
        private readonly Label phaseText = Theme.Label("", 18, bold: true);
        private readonly Label description = Theme.Label("", 10, Theme.Muted);
        private readonly Label ssid = Theme.Label("", bold: true);
        private readonly Label wan = Theme.Label("");
        private readonly Label sharing = Theme.Label("");
        private readonly Label clients = Theme.Label("");
        private readonly PowerButton power = new PowerButton();
        private readonly NotifyIcon tray = new NotifyIcon { Icon = Theme.Brand, Text = "ПОМОГАТОР Wi-Fi" };
        private readonly ToolStripMenuItem trayState = new ToolStripMenuItem { Enabled = false };
        private readonly ToolStripMenuItem trayPower = new ToolStripMenuItem();
        private readonly Timer timer = new Timer { Interval = 4300 };
        private readonly RecoveryEdge recovery = new RecoveryEdge();
        private readonly bool testing;
        private WifiReport report = new WifiReport { Phase = WifiPhase.Unavailable };
        private UserSettings settings = new UserSettings();
        private Task<WifiReport> reading;
        private bool busy, polling, exiting, internetWasAvailable;
        private bool? internet;
        private DateTime internetChecked;
        private string message;
        private WifiPhase shown;

        internal WifiWindow(bool testing = false) : base("ПОМОГАТОР Wi-Fi", new Size(490, 578))
        {
            this.testing = testing;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            var root = Rows(64, 236, 167, 64);
            root.Padding = new Padding(24, 15, 24, 10);
            root.Controls.Add(Header(), 0, 0);
            root.Controls.Add(CenterPanel(), 0, 1);
            root.Controls.Add(StatusPanel(), 0, 2);
            root.Controls.Add(Footer(), 0, 3);
            Controls.Add(root);
            ConfigureTray();
            timer.Tick += async (_, __) => await Poll();
            if (!testing) Shown += async (_, __) => await Startup();
            Present(report);
        }

        private TableLayoutPanel Header()
        {
            var row = Columns(40, 0, 43, 43);
            row.Controls.Add(new PictureBox { Image = Theme.Brand.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
                Dock = DockStyle.Fill, Margin = new Padding(0, 8, 2, 17), BackColor = Color.Transparent }, 0, 0);
            var title = Theme.Label("ПОМОГАТОР Wi-Fi", 15, bold: true);
            title.Padding = new Padding(8, 0, 0, 0);
            row.Controls.Add(title, 1, 0);
            var gear = Theme.Button("⚙", (_, __) => OpenSettings());
            gear.AccessibleName = "Настройки";
            gear.Margin = new Padding(3, 13, 2, 13);
            row.Controls.Add(gear, 2, 0);
            var minimize = Theme.Button("−", (_, __) => WindowState = FormWindowState.Minimized);
            minimize.AccessibleName = "Свернуть";
            minimize.Margin = new Padding(3, 13, 2, 13);
            row.Controls.Add(minimize, 3, 0);
            return row;
        }

        private GlassPanel CenterPanel()
        {
            var panel = new GlassPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 10), Padding = new Padding(14, 8, 14, 7) };
            var contents = Rows(136, 38, 0);
            var center = Columns(0, 146, 0);
            power.Dock = DockStyle.Fill;
            power.Margin = new Padding(0, 1, 0, 0);
            power.Click += async (_, __) => await TogglePower();
            center.Controls.Add(power, 1, 0);
            phaseText.TextAlign = ContentAlignment.MiddleCenter;
            description.TextAlign = ContentAlignment.TopCenter;
            contents.Controls.Add(center, 0, 0);
            contents.Controls.Add(phaseText, 0, 1);
            contents.Controls.Add(description, 0, 2);
            panel.Controls.Add(contents);
            return panel;
        }

        private GlassPanel StatusPanel()
        {
            var panel = new GlassPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 9), Padding = new Padding(17, 8, 17, 8) };
            var table = Rows(35, 35, 35, 35);
            AddFact(table, 0, "Имя сети", ssid);
            AddFact(table, 1, "Интернет на ПК", wan);
            AddFact(table, 2, "Общий доступ (ICS)", sharing);
            AddFact(table, 3, "Устройства", clients);
            panel.Controls.Add(table);
            return panel;
        }

        private static void AddFact(TableLayoutPanel parent, int index, string name, Label value)
        {
            var row = Columns(161, 0);
            row.Controls.Add(Theme.Label(name, 10, Theme.Muted), 0, 0);
            row.Controls.Add(value, 1, 0);
            parent.Controls.Add(row, 0, index);
        }

        private TableLayoutPanel Footer()
        {
            var row = Columns(0, 0, 66);
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var setup = Theme.Button("Настроить сеть", (_, __) => OpenSettings());
            var diagnostics = Theme.Button("Диагностика", (_, __) => OpenDiagnostics());
            setup.Margin = new Padding(0, 7, 7, 11);
            diagnostics.Margin = new Padding(1, 7, 7, 11);
            row.Controls.Add(setup, 0, 0);
            row.Controls.Add(diagnostics, 1, 0);
            var version = Theme.Label("v0.2.0", 9, Theme.Muted);
            version.TextAlign = ContentAlignment.MiddleRight;
            row.Controls.Add(version, 2, 0);
            return row;
        }

        internal static TableLayoutPanel Rows(params int[] heights)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length,
                BackColor = Color.Transparent, Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var height in heights) table.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            return table;
        }

        internal static TableLayoutPanel Columns(params int[] widths)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = widths.Length, RowCount = 1,
                BackColor = Color.Transparent, Margin = Padding.Empty };
            foreach (var width in widths) table.ColumnStyles.Add(width == 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, width));
            return table;
        }

        private void ConfigureTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть ПОМОГАТОР", null, (_, __) => ShowWindow());
            menu.Items.Add(trayState);
            menu.Items.Add(trayPower);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, __) => { if (!busy) { exiting = true; Close(); } });
            trayPower.Click += async (_, __) => await TogglePower();
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, __) => ShowWindow();
            tray.Visible = !testing;
        }

        internal void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        internal void EnableTrayForSmokeTest() => tray.Visible = true;
        internal bool TrayVisible => tray.Visible;
        internal WifiPhase ShownPhase => shown;
        internal bool CanToggle => power.Enabled;
        internal void SetTrayPreference(bool enabled) => settings.CloseToTray = enabled;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && (busy || settings.CloseToTray))
            {
                e.Cancel = true;
                if (!busy) Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                tray.Visible = false;
                tray.ContextMenuStrip?.Dispose();
                tray.Dispose();
                statusIcon?.Dispose();
            }
            base.Dispose(disposing);
        }

        internal void Present(WifiReport value, WifiPhase? transient = null)
        {
            report = value;
            shown = transient ?? value.Phase;
            var state = Title(shown);
            phaseText.Text = Symbol(shown) + "  " + state;
            phaseText.ForeColor = Theme.ColorOf(shown);
            description.Text = message == null ? Explanation(shown, value) :
                (value.Phase == WifiPhase.Owned ? "Точка Wi-Fi работает. " : "") + message;
            ssid.Text = value.Ssid ?? (value.Phase == WifiPhase.External ? "Не удалось определить" : settings.Ssid ?? "Не задано");
            wan.Text = internet == true ? (value.Pppoe == true ? "Доступен · PPPoE подключён" : "Доступен") :
                internet == false ? "Доступ не подтверждён" : value.Pppoe == true ? "PPPoE подключён" : "Не удалось проверить";
            sharing.Text = SharingText(value.Sharing);
            clients.Text = value.Peers.HasValue ? value.Peers.Value.ToString() :
                value.Phase == WifiPhase.Off ? "—" : "Недоступно";
            power.Phase = shown;
            power.Enabled = !busy && (value.Phase == WifiPhase.Off || value.Phase == WifiPhase.Owned);
            power.AccessibleName = value.Phase == WifiPhase.Owned ? "Выключить Wi-Fi" : "Включить Wi-Fi";
            trayState.Text = "Wi-Fi: " + state;
            trayPower.Text = power.AccessibleName;
            trayPower.Enabled = power.Enabled;
            tray.Text = "ПОМОГАТОР Wi-Fi · " + state;
            if (!testing) UpdateTrayIcon(shown);
        }

        private Icon statusIcon;
        private WifiPhase? iconPhase;

        private void UpdateTrayIcon(WifiPhase phase)
        {
            if (iconPhase == phase) return;
            using (var brand = Theme.Brand.ToBitmap())
            using (var bitmap = new Bitmap(brand, new Size(32, 32)))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Theme.ColorOf(phase)))
            using (var border = new SolidBrush(Theme.Input))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillEllipse(border, 21, 1, 11, 11);
                graphics.FillEllipse(brush, 23, 3, 7, 7);
                var handle = bitmap.GetHicon();
                try
                {
                    using (var original = Icon.FromHandle(handle))
                    {
                        var icon = (Icon)original.Clone();
                        tray.Icon = icon;
                        statusIcon?.Dispose();
                        statusIcon = icon;
                        iconPhase = phase;
                    }
                }
                finally { DestroyIcon(handle); }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

        internal static string Title(WifiPhase phase)
        {
            switch (phase)
            {
                case WifiPhase.Owned: return "Wi-Fi включён";
                case WifiPhase.External: return "Внешняя точка работает";
                case WifiPhase.Starting: return "Включение Wi-Fi";
                case WifiPhase.Stopping: return "Отключение Wi-Fi";
                case WifiPhase.Error: return "Требуется внимание";
                case WifiPhase.Unavailable: return "Wi-Fi недоступен";
                default: return "Wi-Fi выключен";
            }
        }

        private static string Symbol(WifiPhase phase) => phase == WifiPhase.Owned || phase == WifiPhase.External ? "✓" :
            phase == WifiPhase.Unavailable || phase == WifiPhase.Error ? "!" : "○";

        private static string Explanation(WifiPhase phase, WifiReport report)
        {
            switch (phase)
            {
                case WifiPhase.Owned: return report.Sharing == SharingState.Verified ? "Точка под управлением ПОМОГАТОРА. Windows подтвердила общий доступ." :
                    "Точка работает. Общий доступ можно проверить в диагностике.";
                case WifiPhase.External: return report.Mechanism == "Hosted Network" ?
                    "Hosted Network запущена извне. Для перехода остановите её там, где включили." :
                    "Точкой управляет Windows или другая программа. Проверьте «Мобильный хот-спот» в Windows.";
                case WifiPhase.Starting: return "Проверяем подключение и запускаем точку доступа.";
                case WifiPhase.Stopping: return "Завершаем сеанс ПОМОГАТОРА.";
                case WifiPhase.Unavailable: return report.HasWifi == false ? "Подключите USB Wi-Fi-адаптер и откройте диагностику." :
                    "Проверьте поддержку Hosted Network и службу WLAN в диагностике.";
                default: return report.Owner == Guid.Empty ? "Нажмите кнопку, чтобы запустить точку доступа." :
                    "Сеанс приостановлен. Нажмите кнопку, чтобы восстановить точку.";
            }
        }

        internal static string SharingText(SharingState state) => state == SharingState.Verified ? "Подтверждён Windows" :
            state == SharingState.Disabled ? "Выключен" : state == SharingState.Other ? "Другая или неполная настройка" : "Не удалось проверить";

        private async Task<WifiReport> Read()
        {
            if (reading == null || reading.IsCompleted) reading = NetworkReading.OnSta(NetworkReading.Read);
            if (await Task.WhenAny(reading, Task.Delay(6500)) != reading)
            {
                Local.Log("network_read_timeout");
                return new WifiReport { Phase = WifiPhase.Unavailable, Mechanism = "Истекло время проверки Windows" };
            }
            return await reading;
        }

        private async Task Startup()
        {
            try { settings = Local.Load<UserSettings>(Local.SettingsPath) ?? new UserSettings(); }
            catch (Exception error) { Local.LogError("settings_read_failed", error); message = "Не удалось прочитать параметры. Сохраните их заново."; }
            busy = true;
            try
            {
                await ReadInternet();
                var current = await Read();
                Present(current);
                if (WifiRules.AutoStart(settings.AutoWifi, current.Phase,
                    current.Owner != Guid.Empty || !string.IsNullOrEmpty(settings.Password)))
                {
                    if (current.Owner == Guid.Empty) await Start(true);
                    else await Recover(current.Owner);
                    Present(await Read());
                }
            }
            catch (Exception error) { Failed(error); }
            finally { busy = false; Present(report, message == null ? (WifiPhase?)null : WifiPhase.Error); timer.Start(); }
        }

        private async Task ReadInternet()
        {
            internet = await NetworkReading.Internet();
            if (internet == true) internetWasAvailable = true;
            internetChecked = DateTime.UtcNow;
        }

        private async Task Poll()
        {
            if (busy || polling) return;
            polling = true;
            try
            {
                var current = await Read();
                if (busy || IsDisposed) return;
                Present(current, message == null ? null : (WifiPhase?)WifiPhase.Error);
                var upstream = current.Pppoe == false || internetWasAvailable && internet == false ? false : current.Pppoe;
                if (recovery.Observe(settings.AutoRestore, current.Owner != Guid.Empty, upstream))
                {
                    await Execute(async () =>
                    {
                        if (current.Phase == WifiPhase.Off)
                        {
                            Present(current, WifiPhase.Starting);
                            await Recover(current.Owner);
                            current = await Read();
                            Present(current);
                        }
                        if (current.CanRepair && current.OwnedLease != null && WifiRules.AutomaticTarget(current.OwnedLease.Target, current.Adapter))
                        {
                            var result = await IcsWorker.Run(new SharingCommand { Action = "repair", Pair = current.OwnedLease });
                            if (!result.Success) throw new WifiError(result.Message);
                        }
                    });
                }
                if (DateTime.UtcNow - internetChecked > TimeSpan.FromSeconds(30))
                {
                    await ReadInternet();
                    if (!IsDisposed && !busy) Present(report, message == null ? null : (WifiPhase?)WifiPhase.Error);
                }
            }
            catch (Exception error) { if (!IsDisposed) Failed(error); }
            finally { polling = false; }
        }

        private async Task TogglePower()
        {
            if (busy || !power.Enabled) return;
            if (report.Phase == WifiPhase.Off && report.Owner == Guid.Empty && string.IsNullOrEmpty(settings.Password)) { OpenSettings(); return; }
            await Execute(async () =>
            {
                var current = await Read();
                Present(current);
                if (current.Phase == WifiPhase.Off && current.Owner != Guid.Empty)
                {
                    Present(current, WifiPhase.Starting);
                    await Recover(current.Owner);
                }
                else if (current.Phase == WifiPhase.Off) await Start(false);
                else if (current.Phase == WifiPhase.Owned) await Stop(current);
            });
        }

        private static async Task Recover(Guid owner)
        {
            var result = await NetworkReading.OnSta(() => PomogatorWifi.Owner.Request(new OwnerRequest { Command = "recover", Owner = owner }));
            if (result == null || !result.Active || result.Owner != owner || result.Error != null)
                throw new WifiError(result?.Error ?? "Windows не подтвердила восстановление точки Wi-Fi. Откройте диагностику.");
        }

        private async Task Start(bool automatic)
        {
            Present(report, WifiPhase.Starting);
            var check = ReadInternet();
            await check;
            var start = await NetworkReading.OnSta(() => PomogatorWifi.Owner.Start(settings.Ssid, settings.Password, automatic));
            if (!start.Active || start.Error != null) throw new WifiError(start.Error ?? "Windows не подтвердила запуск Wi-Fi.");
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var current = await Read();
                if (current.Phase != WifiPhase.Owned || current.Owner != start.Owner)
                    throw new WifiError("Windows не подтвердила, что точка остаётся включённой.");
                Present(current);
                if (automatic || current.Candidate != null || current.Sharing == SharingState.Verified || current.Pppoe != true) break;
                await Task.Delay(300);
            }
            if (!automatic && report.Candidate != null) await AskSharing();
        }

        private async Task AskSharing()
        {
            var candidate = report.Candidate ?? (report.CanRepair ? report.OwnedLease : null);
            if (candidate == null) return;
            var pair = report.PairDescription ?? "Будет восстановлена ранее подтверждённая пара PPPoE и адаптера точки доступа.";
            if (MessageBox.Show(this, pair + "\r\n\r\nРазрешить изменение общего доступа Windows (ICS)? Windows может запросить права администратора только для этой операции.",
                "Подтверждение ICS", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var result = await IcsWorker.Run(new SharingCommand { Action = report.CanRepair ? "repair" : "enable", Pair = candidate });
            if (!result.Success) throw new WifiError(result.Message);
        }

        private async Task Stop(WifiReport current)
        {
            if (current.OwnedLease != null)
            {
                if (MessageBox.Show(this, "Устройства потеряют связь с этой точкой. Вернуть исходный общий доступ для пары PPPoE и виртуального адаптера? Для этого Windows может запросить права администратора.",
                    "Выключить Wi-Fi", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                Present(current, WifiPhase.Stopping);
                var restored = await IcsWorker.Run(new SharingCommand { Action = "restore", Pair = current.OwnedLease });
                if (!restored.Success && MessageBox.Show(this, restored.Message +
                    "\r\n\r\nЗавершить только сеанс Wi-Fi ПОМОГАТОРА? Настройки ICS останутся в текущем состоянии.",
                    "Выключить Wi-Fi", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    throw new WifiError(restored.Message);
            }
            Present(current, WifiPhase.Stopping);
            var result = await NetworkReading.OnSta(() => PomogatorWifi.Owner.Request(new OwnerRequest { Command = "stop", Owner = current.Owner }));
            if (result == null || result.Error != null || result.Owner != Guid.Empty)
                throw new WifiError(result?.Error ?? "Windows не подтвердила завершение сеанса.");
        }

        private async Task Execute(Func<Task> action)
        {
            if (busy) return;
            busy = true;
            message = null;
            Present(report);
            try { await action(); }
            catch (Exception error) { Failed(error); }
            finally
            {
                if (!IsDisposed)
                {
                    try { report = await Read(); }
                    catch (Exception error) { Failed(error); }
                    busy = false;
                    if (!IsDisposed) Present(report, message == null ? null : (WifiPhase?)WifiPhase.Error);
                }
            }
        }

        private void Failed(Exception error)
        {
            Local.LogError("wifi_action_failed", error);
            message = error is WifiError ? error.Message : "Операция не завершена. Откройте диагностику и повторите проверку.";
        }

        private void OpenSettings()
        {
            if (busy) return;
            using (var view = new SettingsWindow(settings, report.Phase))
                if (view.ShowDialog(this) == DialogResult.OK)
                {
                    settings = view.Result;
                    message = null;
                    Present(report);
                }
        }

        private void OpenDiagnostics()
        {
            if (busy) return;
            using (var view = new DiagnosticsWindow(report, internet, async () =>
            {
                await ReadInternet();
                return Tuple.Create(await Read(), internet);
            }))
            {
                var result = view.ShowDialog(this);
                if (result == DialogResult.Retry) _ = Execute(async () => { Present(await Read()); await AskSharing(); });
            }
        }
    }
}
