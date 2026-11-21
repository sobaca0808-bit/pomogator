using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 3000 };
        private HostedNetwork wlan;
        private Session session;
        private bool busy;
        private bool checksPassed;
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
            Add(layout, message);
            var log = new LinkLabel { Text = "Открыть диагностический журнал", AutoSize = true };
            log.LinkClicked += (sender, args) => OpenLog();
            Add(layout, log);
            Controls.Add(layout);

            start.Click += (sender, args) => Run(StartWifi);
            stop.Click += (sender, args) => Run(() => StopWifi(true));
            timer.Tick += (sender, args) => RefreshState();
            Shown += (sender, args) => { Log.Write("application_started"); RefreshState(); timer.Start(); };
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
            start.Enabled = !busy && !owned && checksPassed;
            stop.Enabled = !busy && owned;
            name.Enabled = password.Enabled = !busy && !owned;
        }

        private void RefreshState()
        {
            if (busy) return;
            Guid target;
            var state = HostedNetwork.State.Unavailable;
            checksPassed = false;
            try
            {
                if (wlan == null) wlan = new HostedNetwork();
                state = wlan.Query(out target);
                access.Text = "Точка доступа: " + (state == HostedNetwork.State.Active ? "включена" :
                    state == HostedNetwork.State.Idle ? "выключена" : "Hosted Network недоступна");
                sharing.Text = "Раздача интернета: " + (state == HostedNetwork.State.Active ? "проверяется" : "выключена");
                using (var snapshot = new IcsSnapshot())
                {
                    if (state == HostedNetwork.State.Unavailable)
                        throw new WifiError("Hosted Network недоступна. Проверьте, подключён ли Ralink, и выполните netsh wlan show drivers.");
                    var source = session == null ? Policy.ChooseSource(snapshot.Connections).Id : session.Source;
                    if (state == HostedNetwork.State.Active)
                        sharing.Text = snapshot.IsSharing(source, target)
                            ? "Раздача интернета: ICS настроен"
                            : "Раздача интернета: ICS не настроен или PPPoE отключено";
                    checksPassed = state == HostedNetwork.State.Idle;
                    if (state == HostedNetwork.State.Active && !wlan.Started)
                        throw new WifiError("Точка уже работает. Чтобы настроить её здесь, сначала выключите её тем способом, которым запустили.");
                }
                if (lastCheckError != null)
                {
                    if (message.Text == lastCheckError) message.Text = "";
                    lastCheckError = null;
                    Log.Write("checks_recovered");
                }
            }
            catch (Exception error)
            {
                if (state == HostedNetwork.State.Unavailable) access.Text = "Точка доступа: недоступна";
                if (state != HostedNetwork.State.Active) sharing.Text = "Раздача интернета: выключена";
                else if (sharing.Text == "Раздача интернета: проверяется")
                    sharing.Text = "Раздача интернета: не удалось проверить";
                var info = Explain(error);
                if (lastCheckError != info)
                {
                    lastCheckError = info;
                    Log.Error("checks_failed", error);
                    message.Text = info;
                }
            }
            Buttons();
        }

        private void StartWifi()
        {
            var ssid = Policy.EncodeSsid(name.Text);
            Policy.CheckPassword(password.Text);
            Connection source;
            using (var snapshot = new IcsSnapshot()) source = Policy.ChooseSource(snapshot.Connections);
            Log.Write("pppoe_detected " + source.Id);
            try
            {
                message.Text = "Запуск Hosted Network и поиск виртуального адаптера…";
                message.Update();
                wlan.Start(ssid, password.Text);
                Log.Write("hosted_network_started");
                session = Session.Find(wlan, source);
                Log.Write("hosted_adapter_detected " + session.Target);
                if (session.NeedsSharing())
                {
                    if (MessageBox.Show(this, session.Pair + "\r\n\r\nВключить общий доступ Windows (ICS) для этой пары? " +
                        "При ошибке утилита попробует отменить внесённые изменения.",
                        "Подтверждение общего доступа", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                        != DialogResult.Yes)
                    {
                        StopWifi(false);
                        message.Text = "Настройка общего доступа отменена.";
                        return;
                    }
                    session.Enable(wlan);
                    Log.Write("ics_verified");
                }
                else Log.Write("existing_ics_pair_reused");
                password.Clear();
                message.Text = "Подключите смартфон к Wi-Fi и проверьте, открывается ли сайт.";
            }
            catch (Exception error)
            {
                try { StopWifi(false); }
                catch (Exception cleanup)
                {
                    Log.Error("rollback_failed", cleanup);
                    ShowError(error);
                    message.Text += " " + Explain(cleanup) + " Повторите «Выключить Wi-Fi».";
                    return;
                }
                throw;
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
            // Only application-defined messages and numeric Windows codes are written.
            Write(string.Format("{0} {1} 0x{2:X8} {3}", action, cause.GetType().Name, cause.HResult,
                cause is WifiError ? cause.Message : ""));
        }
    }
}
