using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace PomogatorWifi
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    MessageBox.Show("Запустите ПОМОГАТОР Wi-Fi от имени администратора.",
                        "ПОМОГАТОР Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

            bool first;
            using (var instance = new Mutex(true, @"Global\PomogatorWifi.0.1", out first))
            {
                if (first) Application.Run(new WifiForm());
                else MessageBox.Show("ПОМОГАТОР Wi-Fi уже запущен.", "ПОМОГАТОР Wi-Fi");
            }
        }
    }

    internal sealed class WifiForm : Form
    {
        private readonly TextBox name = new TextBox { Text = "POMOGATOR", Dock = DockStyle.Fill };
        private readonly TextBox password = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        private readonly Button start = new Button { Text = "Включить Wi-Fi", AutoSize = true };
        private readonly Button stop = new Button { Text = "Выключить Wi-Fi", AutoSize = true };
        private readonly Label access = new Label { AutoSize = true };
        private readonly Label sharing = new Label { AutoSize = true };
        private readonly Label message = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly Label checkMessage = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly Label icsDiagnostic = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 3000 };
        private HostedNetwork wlan;
        private Session session;
        private bool busy;
        private bool wlanReady;
        private bool icsChecked;
        private bool setupFailed;
        private string[] lastIcsIssues = Array.Empty<string>();
        private string lastCheckError;

        internal WifiForm()
        {
            Text = "ПОМОГАТОР Wi-Fi";
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 365);
            MinimumSize = new Size(500, 360);
            Font = new Font("Segoe UI", 10);
            var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, Padding = new Padding(16), AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Add(layout, new Label { Text = "Имя Wi-Fi", AutoSize = true });
            Add(layout, name);
            Add(layout, new Label { Text = "Пароль", AutoSize = true });
            Add(layout, password);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
            actions.Controls.Add(start);
            actions.Controls.Add(stop);
            Add(layout, actions);
            Add(layout, access);
            Add(layout, sharing);
            Add(layout, checkMessage);
            Add(layout, icsDiagnostic);
            Add(layout, message);
            var log = new LinkLabel { Text = "Открыть диагностический журнал", AutoSize = true };
            log.LinkClicked += (sender, args) => OpenLog();
            Add(layout, log);
            Controls.Add(layout);

            start.Click += (sender, args) => Run(StartWifi);
            stop.Click += (sender, args) => Run(() => StopWifi(true));
            timer.Tick += (sender, args) => RefreshState();
            Shown += (sender, args) => { Log.Write("application_started version=0.1.2"); RefreshState(); timer.Start(); };
            FormClosing += (sender, args) =>
            {
                if (busy) { args.Cancel = true; return; }
                if (HasOwnedChanges)
                {
                    Run(() => StopWifi(true));
                    args.Cancel = HasOwnedChanges;
                }
            };
            FormClosed += (sender, args) => { timer.Dispose(); if (wlan != null) wlan.Dispose(); };
            Buttons();
        }

        private static void Add(TableLayoutPanel layout, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(3, 3, 3, 5);
            layout.Controls.Add(control, 0, layout.RowCount++);
        }

        private void Run(Action action)
        {
            if (busy) return;
            busy = true;
            Buttons();
            try { action(); }
            catch (Exception error) { ShowError(error); }
            finally { busy = false; RefreshState(); }
        }

        private bool HasOwnedChanges
        {
            get { return (wlan != null && wlan.Started) || (session != null && session.OwnsChanges); }
        }

        private void Buttons()
        {
            bool owned = HasOwnedChanges;
            start.Enabled = !busy && !owned && wlanReady;
            stop.Enabled = !busy && owned;
            name.Enabled = password.Enabled = !busy && !owned;
        }

        private void RefreshState()
        {
            if (busy) return;
            wlanReady = false;
            string stage = "wlan_open";
            try
            {
                if (wlan == null) wlan = new HostedNetwork();
                stage = "wlan_status";
                Guid target;
                var state = wlan.Query(out target);
                access.Text = "Точка доступа: " + (state == HostedNetwork.State.Active ? "включена" :
                    state == HostedNetwork.State.Idle ? "выключена" : "Hosted Network недоступна");
                wlanReady = state == HostedNetwork.State.Idle;
                if (state == HostedNetwork.State.Unavailable)
                    throw new WifiError("Hosted Network недоступна. Проверьте Ralink и выполните netsh wlan show drivers.");

                stage = "ics_snapshot";
                using (var snapshot = OpenIcsSnapshot())
                {
                    if (!icsChecked) Log.Write("ics_snapshot_ok connections=" + snapshot.Connections.Count);
                    icsChecked = true;
                    snapshot.RequireVerified(Guid.Empty, target);
                    stage = "pppoe_select";
                    var source = session == null ? Policy.ChooseSource(snapshot.Connections).Id : session.Source;
                    sharing.Text = "Раздача интернета: " +
                        (state != HostedNetwork.State.Active ? "выключена" :
                            setupFailed ? "ошибка настройки ICS" :
                            snapshot.IsSharing(source, target) ? "ICS настроен" :
                            "ICS не настроен или PPPoE отключено");
                }
                if (lastCheckError != null) Log.Write("checks_recovered");
                lastCheckError = null;
                checkMessage.Text = state == HostedNetwork.State.Active && !wlan.Started
                    ? "Точка уже работает. Выключите её тем способом, которым запустили, чтобы настроить её здесь."
                    : "";
            }
            catch (Exception error)
            {
                if (stage == "wlan_open" || stage == "wlan_status")
                {
                    access.Text = "Точка доступа: недоступна";
                    sharing.Text = "Раздача интернета: не проверена";
                }
                else sharing.Text = "Раздача интернета: проверка не пройдена";

                if (stage == "ics_snapshot") icsChecked = false;
                var info = Explain(error);
                var fingerprint = stage + "|" + info;
                if (lastCheckError != fingerprint)
                {
                    lastCheckError = fingerprint;
                    Log.Error("checks_failed stage=" + stage, error);
                }
                checkMessage.Text = info + (wlanReady ? " Точку Wi-Fi можно включить отдельно." : "");
            }
            Buttons();
        }

        private IcsSnapshot OpenIcsSnapshot()
        {
            var snapshot = new IcsSnapshot();
            var issueLines = snapshot.Issues.Select(issue =>
                (issue.Unsupported ? "ics_object_skipped " : "ics_object_unverified ") + issue.Detail).ToArray();
            foreach (var line in issueLines.Except(lastIcsIssues))
                Log.Write(line);
            lastIcsIssues = issueLines;
            var first = snapshot.Issues.FirstOrDefault(issue => !issue.Unsupported) ?? snapshot.Issues.FirstOrDefault();
            icsDiagnostic.Text = first == null ? "" :
                (first.Unsupported ? "Неподдерживаемый объект ICS пропущен. " : "Ошибка чтения объекта ICS. ") +
                first.Detail + (snapshot.Issues.Count > 1 ? " Остальные записи — в журнале." : "");
            return snapshot;
        }

        private void StartWifi()
        {
            var ssid = Policy.EncodeSsid(name.Text);
            Policy.CheckPassword(password.Text);
            message.Text = "Запуск Hosted Network…";
            message.Update();
            wlan.Start(ssid, password.Text);
            Log.Write("hosted_network_started");
            password.Clear();

            setupFailed = false;
            var stage = "hosted_adapter_find";
            try
            {
                message.Text = "Поиск виртуального адаптера…";
                message.Update();
                session = Session.Find(wlan.Query, OpenIcsSnapshot);
                Log.Write("pppoe_detected " + session.Source);
                Log.Write("hosted_adapter_detected " + session.Target);
                stage = "ics_configure";
                var result = session.ConfigureSharing(pair => MessageBox.Show(this,
                    pair + "\r\n\r\nВключить общий доступ Windows (ICS) для этой пары? " +
                    "При ошибке утилита попробует отменить внесённые изменения.",
                    "Подтверждение общего доступа", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                    == DialogResult.Yes);
                if (result == SharingResult.Declined)
                {
                    Log.Write("ics_confirmation_declined");
                    message.Text = "Точка Wi-Fi включена. Общий доступ не изменён.";
                    return;
                }
                Log.Write(result == SharingResult.Existing ? "existing_ics_pair_reused" : "ics_verified");
                message.Text = "Windows подтвердила настройки ICS. Подключите смартфон к Wi-Fi и проверьте доступ в интернет.";
            }
            catch (Exception error)
            {
                setupFailed = true;
                Log.Error("ics_setup_failed stage=" + stage, error);
                message.Text = Session.SetupFailureMessage(error);
                if (session != null && session.OwnsChanges)
                {
                    try
                    {
                        session.Restore();
                        Log.Write("ics_rollback_ok");
                    }
                    catch (Exception cleanup)
                    {
                        Log.Error("ics_rollback_failed", cleanup);
                        message.Text += " " + Explain(cleanup) + " Повторите «Выключить Wi-Fi».";
                    }
                }
            }
        }

        private void StopWifi(bool confirm)
        {
            if (session != null && session.OwnsChanges)
            {
                if (confirm && MessageBox.Show(this, session.Pair + "\r\n\r\nВыключить общий доступ, включённый утилитой?",
                    "Подтверждение общего доступа", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                    != DialogResult.Yes) return;
                session.Restore();
                Log.Write("ics_restored");
            }
            if (wlan != null && wlan.Started)
            {
                wlan.Stop();
                Log.Write("hosted_network_stopped");
            }
            session = null;
            setupFailed = false;
            message.Text = "Точка доступа выключена.";
        }

        private void ShowError(Exception error)
        {
            Log.Error("operation_failed", error);
            message.Text = Explain(error);
        }

        private static string Explain(Exception error)
        {
            if (error is WifiError) return error.Message;
            if (error is IcsFailure ics)
                return ics.InnerException is WifiError ? ics.InnerException.Message : ics.Message;
            if (error is EntryPointNotFoundException || error is DllNotFoundException)
                return "Windows не предоставляет интерфейс Hosted Network. Проверьте версию Windows и драйвер Ralink.";
            return string.Format("Windows не выполнила сетевую операцию (0x{0:X8}). Проверьте службу ICS и вкладку «Доступ» в ncpa.cpl.",
                error.GetBaseException().HResult);
        }

        private void OpenLog()
        {
            if (Log.Write("log_open_requested"))
            {
                try { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); return; }
                catch (Exception) { }
            }
            message.Text = "Не удалось открыть журнал в папке %LOCALAPPDATA%\\PomogatorWifi.";
        }
    }

    internal static class Log
    {
        internal static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PomogatorWifi", "diagnostic.log");

        internal static bool Write(string item)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, DateTimeOffset.Now.ToString("O") + " " + item + Environment.NewLine);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void Error(string action, Exception error)
        {
            var cause = error.GetBaseException();
            if (error is IcsFailure ics) action += " operation=" + ics.Operation;
            // Only application-defined messages and numeric Windows codes are written.
            Write(string.Format("{0} {1} 0x{2:X8} {3}", action, cause.GetType().Name, cause.HResult,
                cause is WifiError ? cause.Message : ""));
        }
    }
}
