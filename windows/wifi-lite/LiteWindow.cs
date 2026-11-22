using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PomogatorWifiLite
{
    internal sealed class LiteWindow : Form
    {
        private readonly HostedSession session;
        private readonly IcsProbe ics;
        private readonly TextBox ssid = new TextBox { Text = "POMOGATOR", MaxLength = 32, Dock = DockStyle.Fill };
        private readonly TextBox password = new TextBox { UseSystemPasswordChar = true, MaxLength = 63, Dock = DockStyle.Fill };
        private readonly Button start = new Button { Text = "Запустить", AutoSize = true, Enabled = false };
        private readonly Button stop = new Button { Text = "Остановить", AutoSize = true, Enabled = false };
        private readonly Label point = Info("Точка — проверяем Windows…");
        private readonly Label clients = Info("Клиенты — неизвестно");
        private readonly Label sharing = Info("ICS — проверяем Windows…");
        private readonly Label details = Info("");
        private readonly Timer timer = new Timer { Interval = 1000 };
        private Guid hosted;
        private DateTime nextSharing;
        private bool busy, closing, checkingSharing;
        private string actionFailure;

        internal LiteWindow(HostedSession session, IcsProbe ics)
        {
            this.session = session;
            this.ics = ics;
            Text = "ПОМОГАТОР Wi-Fi Lite";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(510, 375);
            MinimumSize = new Size(410, 340);
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Font;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(16) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Row(Info("Имя Wi-Fi (SSID)"), ssid);
            Row(Info("Пароль WPA2"), password);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 3, 0, 6) };
            buttons.Controls.AddRange(new Control[] { start, stop });
            Row(buttons);
            Row(point);
            Row(clients);
            Row(sharing);
            var internet = Info("Интернет — проверьте сайт на подключённом телефоне с выключенными мобильными данными.");
            Row(internet);
            Row(details);
            Controls.Add(panel);
            panel.SizeChanged += (_, __) =>
            {
                int width = Math.Max(240, panel.ClientSize.Width - 52);
                foreach (var item in new[] { point, clients, sharing, internet, details })
                    item.MaximumSize = new Size(width, 0);
            };
            start.Click += async (_, __) =>
            {
                var name = ssid.Text;
                var secret = password.Text;
                if (await Refresh(() => session.Start(name, secret))) password.Clear();
            };
            stop.Click += async (_, __) => await Refresh(session.Stop);
            timer.Tick += async (_, __) => await Refresh();
            Shown += async (_, __) => { timer.Start(); await Refresh(); };
            FormClosing += (_, args) =>
            {
                timer.Stop();
                if (busy && args.CloseReason == CloseReason.UserClosing)
                {
                    closing = true;
                    args.Cancel = true;
                }
                else if (!busy) session.Dispose();
            };
            FormClosed += (_, __) => timer.Dispose();

            void Row(Control first, Control second = null)
            {
                var index = panel.RowCount++;
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.Controls.Add(first, 0, index);
                if (second == null) panel.SetColumnSpan(first, 2);
                else panel.Controls.Add(second, 1, index);
            }
        }

        private static Label Info(string text) => new Label {
            Text = text, AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(3, 5, 3, 5), TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
        };

        private async Task<bool> Refresh(Action action = null)
        {
            if (busy && action != null)
            {
                start.Enabled = stop.Enabled = false;
                while (busy && !closing && !IsDisposed) await Task.Delay(30);
            }
            if (busy || closing || IsDisposed) return false;
            busy = true;
            if (action != null) start.Enabled = stop.Enabled = false;
            var succeeded = true;
            PointStatus status = null;
            string readFailure = null;
            try
            {
                if (action != null)
                {
                    actionFailure = null;
                    try { await Task.Run(action); }
                    catch (Exception error) { succeeded = false; actionFailure = SafeError(error); }
                }
                try { status = await Task.Run(() => session.Read()); }
                catch (Exception error) { readFailure = SafeError(error); }
                if (IsDisposed) return succeeded;
                Present(status, readFailure);
                _ = RefreshSharing();
                return succeeded;
            }
            finally
            {
                busy = false;
                if (closing && !IsDisposed) Close();
            }
        }

        private void Present(PointStatus status, string error)
        {
            point.Text = "Точка — " + (status == null ? "состояние не подтверждено" :
                status.State == PointState.Active ? (status.Ours ? "запущена Lite" : "запущена извне") :
                status.State == PointState.Idle ? "выключена" : "недоступна");
            clients.Text = "Клиенты — " + (status == null ? "неизвестно" : status.State == PointState.Active ?
                status.Clients.ToString() : "—");
            var note = status?.Detail ?? error;
            if (string.IsNullOrEmpty(note) && status?.State == PointState.Active && status.Clients == 0)
                note = "Windows пока не подтверждает подключение клиентов. Причина отказа может быть видна только на телефоне.";
            details.Text = string.Join(Environment.NewLine, new[] { actionFailure, note }.Where(text => !string.IsNullOrWhiteSpace(text)));
            var adapter = status?.State == PointState.Active ? status.Adapter : Guid.Empty;
            if (adapter != hosted)
            {
                hosted = adapter;
                nextSharing = DateTime.MinValue;
                sharing.Text = "ICS — проверяем Windows…";
            }
            stop.Enabled = session.CanStop;
            start.Enabled = !session.CanStop && status?.State == PointState.Idle && !status.OtherController;
        }

        private async Task RefreshSharing()
        {
            if (checkingSharing || DateTime.UtcNow < nextSharing || closing) return;
            checkingSharing = true;
            var adapter = hosted;
            try
            {
                var pending = ics.BeginRead(adapter);
                if (pending == null) return;
                var ready = await Task.WhenAny(pending, Task.Delay(2200)) == pending;
                if (!IsDisposed && hosted == adapter)
                    sharing.Text = "ICS — " + (ready ? await pending : "истекло время проверки. Состояние не подтверждено.");
            }
            catch (Exception error)
            {
                if (!IsDisposed && hosted == adapter) sharing.Text = "ICS — " + SafeError(error);
            }
            finally
            {
                if (hosted == adapter) nextSharing = DateTime.UtcNow.AddSeconds(3);
                checkingSharing = false;
            }
        }

        private static string SafeError(Exception error) => error is WifiException ? error.Message :
            "Операция Windows не завершена · 0x" + error.HResult.ToString("X8") + ".";
    }
}
