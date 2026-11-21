using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PomogatorNetworkDiagnostics
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--probe")
            {
                RunWorker(args[1]);
                return;
            }
            if (args.Length != 0) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DiagnosticsForm());
        }

        private static void RunWorker(string id)
        {
            var probes = new Dictionary<string, Func<Finding>>(StringComparer.Ordinal) {
                { "ras", WindowsNetwork.Ras }, { "ip", Internet.Ip }, { "dns", Internet.Dns },
                { "proxy", Internet.Proxy }, { "direct", () => Internet.Websites(false) },
                { "system-proxy", () => Internet.Websites(true) },
                { "hosted", WindowsNetwork.Hosted }, { "ics", Ics.Read }
            };
            Func<Finding> probe;
            if (!probes.TryGetValue(id, out probe)) { Environment.ExitCode = 2; return; }

            Finding result;
            try { result = probe(); }
            catch (Exception error)
            {
                result = Finding.Create(Outcome.Review, "Windows не завершила чтение или контрольный запрос.",
                    codes: new[] { Finding.Hex(error.GetBaseException().HResult) });
            }
            using (var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                stdout.WriteLine(Report.Json(result));
        }
    }

    internal sealed class DiagnosticsForm : Form
    {
        private readonly Button run = new Button { Text = "Полная диагностика", AutoSize = true };
        private readonly Button save = new Button { Text = "Сохранить отчёт", AutoSize = true, Enabled = false };
        private readonly Label status = new Label { AutoSize = true };
        private readonly TextBox output = new TextBox {
            Multiline = true, ReadOnly = true, MaxLength = int.MaxValue, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical
        };
        private bool inspecting;
        private Report report;

        internal DiagnosticsForm()
        {
            Text = "ПОМОГАТОР — Диагностика сети";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(830, 590);
            MinimumSize = new Size(540, 360);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            actions.Controls.Add(run);
            actions.Controls.Add(save);
            layout.Controls.Add(actions, 0, 0);
            status.Margin = new Padding(3, 8, 3, 8);
            status.Text = "Режим чтения. Будут выполнены контрольные запросы в интернет.";
            layout.Controls.Add(status, 0, 1);
            output.AccessibleName = "Результаты диагностики";
            layout.Controls.Add(output, 0, 2);
            var journal = new LinkLabel { Text = "Открыть журнал диагностики", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            layout.Controls.Add(journal, 0, 3);
            Controls.Add(layout);
            AcceptButton = run;

            run.Click += async (sender, args) => await Diagnose();
            save.Click += (sender, args) => SaveReport();
            journal.LinkClicked += (sender, args) => OpenJournal();
            FormClosing += (sender, args) =>
            {
                if (inspecting && args.CloseReason == CloseReason.UserClosing) args.Cancel = true;
            };
        }

        private async System.Threading.Tasks.Task Diagnose()
        {
            run.Enabled = save.Enabled = false;
            inspecting = true;
            report = null;
            output.Clear();
            status.Text = "Проверки выполняются. Дождитесь завершения…";
            Journal.Write("diagnostics_started version=0.1.0");
            var progress = new Progress<Finding>(finding =>
            {
                if (inspecting && !IsDisposed)
                    output.AppendText("[" + finding.Label + "] " + finding.Title + Environment.NewLine);
            });
            try
            {
                report = await Runner.Diagnose(Application.ExecutablePath, ((IProgress<Finding>)progress).Report);
                output.Text = report.ToText();
                output.SelectionStart = 0;
                output.ScrollToCaret();
                status.Text = "Проверок: " + report.Results.Length +
                    " · Исправно: " + report.Results.Count(item => item.Status == Outcome.Ok) +
                    " · Проблема: " + report.Results.Count(item => item.Status == Outcome.Problem) +
                    " · Требуется проверка: " + report.Results.Count(item => item.Status == Outcome.Review);
                save.Enabled = true;
                Journal.Write("diagnostics_finished");
            }
            catch (Exception error)
            {
                var code = Finding.Hex(error.GetBaseException().HResult);
                Journal.Write("diagnostics_failed " + code);
                status.Text = "Не удалось завершить диагностику (" + code + "). См. журнал.";
            }
            finally
            {
                inspecting = false;
                run.Enabled = true;
            }
        }

        private void SaveReport()
        {
            if (report == null || MessageBox.Show(this, Report.ExportWarning + "\r\n\r\nСохранить отчёт?",
                "Экспорт отчёта", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            using (var dialog = new SaveFileDialog {
                FileName = "pomogator-network-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
                Filter = "Текстовый отчёт (*.txt)|*.txt|Отчёт JSON (*.json)|*.json",
                DefaultExt = "txt", AddExtension = true, OverwritePrompt = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var json = string.Equals(Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase);
                    if (!json && !string.Equals(Path.GetExtension(dialog.FileName), ".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this, "Укажите расширение .txt или .json.", Text);
                        return;
                    }
                    File.WriteAllText(dialog.FileName, json ? Report.Json(report) : report.ToText(), new UTF8Encoding(!json));
                    Journal.Write(json ? "export_json" : "export_txt");
                    status.Text = "Отчёт сохранён.";
                }
                catch (Exception error)
                {
                    var code = Finding.Hex(error.HResult);
                    Journal.Write("export_failed " + code);
                    MessageBox.Show(this, "Не удалось сохранить отчёт (" + code + "). Проверьте доступ к выбранной папке.", Text);
                }
            }
        }

        private void OpenJournal()
        {
            if (Journal.Write("journal_open_requested"))
            {
                try { Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + Journal.PathName + "\""); return; }
                catch (Exception) { }
            }
            MessageBox.Show(this, "Не удалось открыть журнал: %LOCALAPPDATA%\\PomogatorNetworkDiagnostics\\diagnostic.log", Text);
        }
    }
}
