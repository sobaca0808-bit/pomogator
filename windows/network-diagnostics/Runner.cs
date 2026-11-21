using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PomogatorNetworkDiagnostics
{
    internal sealed class Probe
    {
        internal readonly string Id;
        internal readonly string Title;
        internal readonly int Seconds;
        internal readonly bool PowerShell;
        internal Probe(string id, string title, int seconds, bool powerShell = false)
        { Id = id; Title = title; Seconds = seconds; PowerShell = powerShell; }
    }

    internal static class Runner
    {
        private static readonly Probe[] Probes = {
            new Probe("adapters", "Адаптеры и драйверы", 25, true),
            new Probe("ras", "PPPoE и сеансы RAS/VPN", 10),
            new Probe("addresses", "IP-адреса, DNS, шлюзы и маршруты", 25, true),
            new Probe("ip", "Внешние IP-адреса", 10),
            new Probe("dns", "Разрешение имён DNS", 12),
            new Probe("services", "Сетевые службы Windows", 18, true),
            new Probe("proxy", "Системный прокси", 10),
            new Probe("direct", "Сайты напрямую, без системного прокси", 16),
            new Probe("system-proxy", "Сайты через системный прокси", 22),
            new Probe("hosted", "Hosted Network", 10),
            new Probe("ics", "Общий доступ ICS и COM", 15),
            new Probe("events", "Ошибки в журналах Windows", 25, true),
        };

        internal static async Task<Report> Diagnose(string executable, Action<Finding> completed)
        {
            var report = new Report { StartedAt = Report.Now(), WindowsVersion = Environment.OSVersion.Version.ToString() };
            using (var slots = new SemaphoreSlim(4))
            {
                var tasks = Probes.Select(async probe =>
                {
                    await slots.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var finding = await Run(probe, executable).ConfigureAwait(false);
                        Journal.Result(finding);
                        completed(finding);
                        return finding;
                    }
                    finally { slots.Release(); }
                });
                report.Results = await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            report.Conclusions = Interpretation.Analyze(report.Results);
            report.FinishedAt = Report.Now();
            return report;
        }

        private static async Task<Finding> Run(Probe probe, string executable)
        {
            var watch = Stopwatch.StartNew();
            Finding result;
            try
            {
                var info = Command(probe, executable);
                var json = await ReadProcess(info, TimeSpan.FromSeconds(probe.Seconds)).ConfigureAwait(false);
                result = Report.Parse<Finding>(json);
                if (result == null || !Enum.IsDefined(typeof(Outcome), result.Status) ||
                    string.IsNullOrWhiteSpace(result.Summary) || result.Details == null || result.Codes == null)
                    throw new InvalidDataException();
            }
            catch (TimeoutException)
            {
                result = Finding.Create(Outcome.Review, "Проверка остановлена по тайм-ауту (" + probe.Seconds + " с).",
                    codes: new[] { "TIMEOUT" });
            }
            catch (ProbeExitException error)
            {
                result = Finding.Create(Outcome.Review, "Диагностический процесс завершился с ошибкой.",
                    codes: new[] { "EXIT=" + error.ExitCode });
            }
            catch (Exception error)
            {
                result = Finding.Create(Outcome.Review, "Не удалось получить результат проверки.",
                    codes: new[] { Finding.Hex(error.HResult) });
            }
            result.Id = probe.Id;
            result.Title = probe.Title;
            result.Label = Finding.LabelFor(result.Status);
            result.CheckedAt = Report.Now();
            result.DurationMs = watch.ElapsedMilliseconds;
            return result;
        }

        private static ProcessStartInfo Command(Probe probe, string executable)
        {
            if (!probe.PowerShell) return new ProcessStartInfo(executable, "--probe " + probe.Id);
            var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var powershell = Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            return new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                EncodePowerShell(Resource("common.ps1") + "\r\n" + Resource(probe.Id + ".ps1")));
        }

        internal static string EncodePowerShell(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        private static string Resource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        internal static async Task<string> ReadProcess(ProcessStartInfo info, TimeSpan timeout)
        {
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (var process = new Process { StartInfo = info, EnableRaisingEvents = true })
            using (var deadline = new CancellationTokenSource())
            {
                var exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (sender, args) => exit.TrySetResult(true);
                var delay = Task.Delay(timeout, deadline.Token);
                if (!process.Start()) throw new InvalidOperationException();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                var all = Task.WhenAll(output, errors, exit.Task);
                if (await Task.WhenAny(all, delay).ConfigureAwait(false) != all)
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    throw new TimeoutException();
                }
                deadline.Cancel();
                await all.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new ProbeExitException(process.ExitCode);
                return output.Result;
            }
        }
    }

    internal sealed class ProbeExitException : Exception
    {
        internal int ExitCode { get; }
        internal ProbeExitException(int code) { ExitCode = code; }
    }

    internal static class Journal
    {
        private static readonly object Gate = new object();
        private static readonly Regex SafeCode = new Regex(@"\A(?:0x[0-9A-Fa-f]{8}|[A-Z_]+(?:=[0-9-]+)?)\z");
        internal static readonly string PathName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PomogatorNetworkDiagnostics", "diagnostic.log");

        internal static void Result(Finding finding)
        {
            Write(finding.Id + " " + finding.Status + " " + finding.DurationMs + "ms " +
                string.Join(",", finding.Codes.Where(code => code != null && SafeCode.IsMatch(code)).Distinct()));
        }

        internal static bool Write(string entry)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PathName));
                    File.AppendAllText(PathName, Report.Now() + " " + entry + Environment.NewLine, Encoding.UTF8);
                    return true;
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }
    }
}
