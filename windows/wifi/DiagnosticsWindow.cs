using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PomogatorWifi
{
    internal sealed class DiagnosticsWindow : GlassForm
    {
        private readonly TextBox checks = Box();
        private readonly TextBox log = Box();
        private readonly Button share;
        private readonly Func<Task<Tuple<WifiReport, bool?>>> run;
        private WifiReport report;
        private bool? internet;

        internal DiagnosticsWindow(WifiReport report, bool? internet, Func<Task<Tuple<WifiReport, bool?>>> run)
            : base("Диагностика · ПОМОГАТОР Wi-Fi", new Size(538, 622))
        {
            this.run = run;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            var root = WifiWindow.Rows(50, 198, 37, 0, 53);
            root.Padding = new Padding(20, 9, 20, 7);
            root.Controls.Add(Theme.Label("Диагностика", 19, bold: true), 0, 0);
            root.Controls.Add(checks, 0, 1);
            root.Controls.Add(Theme.Label("Технический журнал", 12, bold: true), 0, 2);
            root.Controls.Add(log, 0, 3);
            var row = WifiWindow.Columns(117, 130, 0);
            var refresh = Theme.Button("Обновить", async (_, __) => await RefreshReport());
            var save = Theme.Button("Экспорт TXT", (_, __) => Export());
            share = Theme.Button("Настроить общий доступ", (_, __) => DialogResult = DialogResult.Retry);
            foreach (var button in new[] { refresh, save, share }) button.Margin = new Padding(0, 8, 6, 5);
            row.Controls.Add(refresh, 0, 0);
            row.Controls.Add(save, 1, 0);
            row.Controls.Add(share, 2, 0);
            root.Controls.Add(row, 0, 4);
            Controls.Add(root);
            LoadReport(report, internet);
            ActiveControl = refresh;
        }

        private static TextBox Box() => new TextBox { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
            Margin = Padding.Empty };

        private static string Verdict(bool? value, string negative = "Требует внимания") =>
            value == true ? "Работает" : value == false ? negative : "Не удалось проверить";

        internal static string Summarize(WifiReport state, bool? online)
        {
            var point = state.Phase == WifiPhase.Owned || state.Phase == WifiPhase.External ? "Работает" :
                state.Phase == WifiPhase.Unavailable ? "Недоступно" : "Требует внимания";
            var detected = state.Sharing == SharingState.Verified ? "Работает · роли подтверждены Windows" :
                state.Sharing == SharingState.Unknown ? "Не удалось проверить" : "Требует внимания · " + WifiWindow.SharingText(state.Sharing);
            return "Wi-Fi-адаптер — " + Verdict(state.HasWifi, "Недоступно") + Environment.NewLine +
                "Механизм — " + (state.Mechanism ?? "Не удалось проверить") + Environment.NewLine +
                "Точка доступа — " + point + " · " + WifiWindow.Title(state.Phase) + Environment.NewLine +
                "Мобильный хот-спот — " + (state.Mobile == MobilePhase.On ? "Работает" : state.Mobile == MobilePhase.Off ? "Выключен" :
                    state.Mobile == MobilePhase.Transition ? "Требует внимания · Windows меняет состояние" : "Не удалось проверить") + Environment.NewLine +
                "PPPoE — " + Verdict(state.Pppoe) + Environment.NewLine +
                "Общий доступ (ICS) — " + detected + Environment.NewLine +
                "HTTPS с компьютера — " + Verdict(online) + Environment.NewLine +
                "Управление — " + (state.Phase == WifiPhase.Owned ? "Сеанс ПОМОГАТОРА" : state.Phase == WifiPhase.External ?
                    "Недоступно · внешний сеанс" : state.Phase == WifiPhase.Off ? "Доступен запуск" : "Не удалось подтвердить") +
                (state.SharingNote == null ? "" : Environment.NewLine + state.SharingNote);
        }

        private void LoadReport(WifiReport state, bool? online)
        {
            report = state;
            internet = online;
            checks.Text = Summarize(report, internet);
            share.Enabled = report.Candidate != null || report.CanRepair;
            try
            {
                if (!File.Exists(Local.LogPath)) { log.Text = "Пока нет записей."; return; }
                using (var stream = new FileStream(Local.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(Math.Max(0, stream.Length - 96 * 1024), SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) log.Text = reader.ReadToEnd();
                }
                log.Select(log.TextLength, 0);
                log.ScrollToCaret();
            }
            catch (IOException) { log.Text = "Журнал сейчас недоступен. Попробуйте обновить."; }
            catch (UnauthorizedAccessException) { log.Text = "Windows запретила доступ к журналу."; }
        }

        private bool refreshing;
        private async Task RefreshReport()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var latest = await run();
                if (!IsDisposed) LoadReport(latest.Item1, latest.Item2);
            }
            catch (Exception error)
            {
                Local.LogError("diagnostics_failed", error);
                if (!IsDisposed) checks.Text = "Не удалось завершить проверку. Попробуйте ещё раз.";
            }
            finally { refreshing = false; }
        }

        private void Export()
        {
            if (MessageBox.Show(this, "Отчёт может содержать дату и идентификаторы сетевых адаптеров. Проверьте содержимое перед передачей.",
                "Экспорт диагностики", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            using (var picker = new SaveFileDialog { Filter = "Текстовый отчёт (*.txt)|*.txt", FileName = "Pomogator-Wifi-diagnostics.txt" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(picker.FileName, "ПОМОГАТОР Wi-Fi 0.2.0 · " + DateTimeOffset.Now.ToString("O") +
                        Environment.NewLine + checks.Text + Environment.NewLine + Environment.NewLine + log.Text, Encoding.UTF8);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { MessageBox.Show(this, "Windows не сохранила отчёт. Выберите другую папку.", "Экспорт диагностики"); }
            }
        }
    }
}
