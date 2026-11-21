using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PomogatorNetworkDiagnostics
{
    internal static class Internet
    {
        private static readonly Uri Connectivity = new Uri("http://www.msftconnecttest.com/connecttest.txt");
        private static readonly Uri SecureSite = new Uri("https://www.microsoft.com/");
        private static readonly Uri[] Sites = { Connectivity, SecureSite };

        internal static Finding Ip()
        {
            var details = new List<string>();
            var codes = new List<string>();
            var successes = 0;
            foreach (var address in new[] { "1.1.1.1", "8.8.8.8" })
            {
                using (var client = new TcpClient())
                {
                    try
                    {
                        if (!client.ConnectAsync(IPAddress.Parse(address), 443).Wait(2800)) throw new TimeoutException();
                        successes++;
                        details.Add(address + ":443 — TCP-соединение установлено.");
                    }
                    catch (Exception error)
                    {
                        var cause = error.GetBaseException();
                        codes.Add(cause is TimeoutException ? "TIMEOUT" : ErrorCode(cause));
                        details.Add(address + ":443 — TCP-соединение не установлено.");
                    }
                }
            }
            details.Add("Запросы используют IP без DNS и выполняются по текущему маршруту Windows. Сетевой фильтр может блокировать отдельную цель или порт.");
            return Finding.Create(Assess(successes, 2), successes == 2
                ? "Оба внешних IP доступны по TCP/443."
                : "Один или оба контрольных TCP-запроса завершились без соединения.", details, codes);
        }

        internal static Finding Dns()
        {
            var details = new List<string>();
            var codes = new List<string>();
            var successes = 0;
            foreach (var site in Sites)
            {
                try
                {
                    var query = DnsQuery(site.Host);
                    if (!query.Wait(4000))
                    {
                        codes.Add("TIMEOUT");
                        details.Add(site.Host + " — системный резолвер не ответил за 4 с.");
                        continue;
                    }
                    var addresses = query.Result;
                    if (addresses.Length == 0) throw new SocketException((int)SocketError.NoData);
                    successes++;
                    details.Add(site.Host + " → " + string.Join(", ", addresses.Select(ip => ip.ToString())));
                }
                catch (Exception error)
                {
                    codes.Add(ErrorCode(error.GetBaseException()));
                    details.Add(site.Host + " — системный резолвер не вернул адрес.");
                }
            }
            details.Add("Используется резолвер Windows, включая его кэш. DNS-проверка браузера или расширения может отличаться.");
            return Finding.Create(Assess(successes, Sites.Length), successes == Sites.Length
                ? "Оба контрольных имени разрешаются."
                : "Не все контрольные имена дали IP-адрес.", details, codes);
        }

        private static System.Threading.Tasks.Task<IPAddress[]> DnsQuery(string host) =>
            System.Net.Dns.GetHostAddressesAsync(host);

        internal static Finding Proxy()
        {
            var details = new List<string>();
            var codes = new List<string>();
            var status = Outcome.Ok;
            using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            {
                var manual = settings?.GetValue("ProxyEnable") is int flag && flag != 0;
                var auto = !string.IsNullOrWhiteSpace(settings?.GetValue("AutoConfigURL") as string);
                details.Add("Internet Settings пользователя: ручной прокси " + (manual ? "включён" : "не включён") +
                    "; URL сценария PAC " + (auto ? "указан." : "не указан.") + " Значения не экспортируются.");
            }

            ProxyInfo info;
            if (WinHttpGetDefaultProxyConfiguration(out info))
            {
                try
                {
                    details.Add("WinHTTP, конфигурация по умолчанию: " +
                        (info.AccessType == 3 ? "именованный прокси." :
                         info.AccessType == 1 ? "прямой доступ." : "режим " + info.AccessType + "."));
                }
                finally
                {
                    if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
                    if (info.Bypass != IntPtr.Zero) GlobalFree(info.Bypass);
                }
            }
            else
            {
                status = Outcome.Review;
                codes.Add("WIN32=" + Marshal.GetLastWin32Error());
                details.Add("Не удалось прочитать конфигурацию WinHTTP по умолчанию.");
            }
            details.Add("PAC, автообнаружение и исключения могут выбрать маршрут отдельно для каждого сайта. Настройки браузерных расширений не считываются.");
            return Finding.Create(status, "Прочитаны доступные системные признаки настройки прокси.", details, codes);
        }

        internal static Finding Websites(bool viaSystemProxy)
        {
            var details = new List<string>();
            var codes = new List<string>();
            var system = viaSystemProxy ? WebRequest.GetSystemWebProxy() : null;
            var attempted = 0;
            var successes = 0;
            var unresolved = false;
            var unexpected = false;
            foreach (var site in Sites)
            {
                Uri proxy = null;
                if (viaSystemProxy)
                {
                    try { proxy = ProxyFor(site, system.GetProxy(site)); }
                    catch (Exception error)
                    {
                        unresolved = true;
                        codes.Add(ErrorCode(error.GetBaseException()));
                        details.Add(site.Host + " — Windows не вернула пригодный адрес системного прокси.");
                        continue;
                    }
                    if (proxy == null)
                    {
                        details.Add(site.Host + " — Windows выбрала прямой доступ; запрос через прокси пропущен.");
                        continue;
                    }
                }
                attempted++;
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(site);
                    request.Proxy = proxy == null ? null : new WebProxy(proxy);
                    request.Timeout = 4200;
                    request.ReadWriteTimeout = 2500;
                    request.AllowAutoRedirect = false;
                    request.UseDefaultCredentials = false;
                    request.PreAuthenticate = false;
                    request.UserAgent = "PomogatorNetworkDiagnostics/0.1";
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        var code = (int)response.StatusCode;
                        codes.Add("HTTP=" + code);
                        var health = HttpOutcome(code, site == Connectivity, site == Connectivity && ExpectedContent(response));
                        if (health == Outcome.Ok) successes++;
                        else unexpected = true;
                        details.Add(site.Host + " — HTTP " + code +
                            (health == Outcome.Ok ? ", контрольный ответ получен." : ", ответ отличается от ожидаемого."));
                    }
                }
                catch (WebException error)
                {
                    using (var response = error.Response as HttpWebResponse)
                    {
                        if (response != null)
                        {
                            var code = (int)response.StatusCode;
                            codes.Add("HTTP=" + code);
                            unexpected = true;
                            details.Add(site.Host + " — сервер ответил HTTP " + code +
                                (code == 407 ? "; прокси требует аутентификации, утилита не передаёт учётные данные." : "."));
                        }
                        else
                        {
                            codes.Add("WEB=" + (int)error.Status);
                            details.Add(site.Host + " — " + WebExplanation(error.Status) + ".");
                        }
                    }
                }
                catch (Exception error)
                {
                    codes.Add(ErrorCode(error.GetBaseException()));
                    details.Add(site.Host + " — не удалось выполнить контрольный запрос.");
                }
            }

            if (viaSystemProxy)
            {
                details.Add("Проверяется прокси Windows для этих URL без учётных данных. PAC и приложение браузера могут выбирать другие маршруты.");
                if (attempted == 0)
                    return Finding.Create(Outcome.Review, unresolved ? "Не удалось определить маршрут через системный прокси."
                        : "Для контрольных адресов системный прокси не выбран.", details, codes);
            }
            else details.Add("Прямой запрос обходится без явного системного прокси и использует текущие маршруты Windows. " +
                "Обход активного VPN не подтверждён. Работа браузерного VPN и отрисовка страницы здесь не проверяются.");

            var outcome = unresolved || unexpected || attempted != Sites.Length
                ? Outcome.Review : Assess(successes, attempted);
            return Finding.Create(outcome, successes == Sites.Length
                ? "Получены ожидаемые ответы от обоих контрольных адресов."
                : "Получены не все ожидаемые ответы. Это результат запросов к контрольным адресам.", details, codes);
        }

        // Discard credentials, path, query, and fragments even when PAC or Windows returns them.
        internal static Uri ProxyFor(Uri destination, Uri choice)
        {
            if (choice == null || destination.Equals(choice)) return null;
            if (!choice.IsAbsoluteUri || !choice.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException();
            return new UriBuilder(choice.Scheme, choice.IdnHost, choice.Port).Uri;
        }

        internal static Outcome Assess(int success, int attempted) =>
            attempted == 0 ? Outcome.Review : success == attempted ? Outcome.Ok :
                success == 0 ? Outcome.Problem : Outcome.Review;

        internal static Outcome HttpOutcome(int status, bool expectsContent, bool contentMatches) =>
            expectsContent ? (status == 200 && contentMatches ? Outcome.Ok : Outcome.Review)
                : (status >= 200 && status < 400 ? Outcome.Ok : Outcome.Review);

        private static bool ExpectedContent(HttpWebResponse response)
        {
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII))
            {
                const string expected = "Microsoft Connect Test";
                var chars = new char[expected.Length + 2];
                var count = reader.ReadBlock(chars, 0, chars.Length);
                return new string(chars, 0, count).TrimEnd('\r', '\n') == expected;
            }
        }

        private static string WebExplanation(WebExceptionStatus status)
        {
            switch (status)
            {
                case WebExceptionStatus.NameResolutionFailure: return "имя сайта не разрешилось";
                case WebExceptionStatus.ProxyNameResolutionFailure: return "имя прокси не разрешилось";
                case WebExceptionStatus.Timeout: return "истекло время ожидания";
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure: return "ошибка сертификата или TLS";
                case WebExceptionStatus.ConnectFailure: return "соединение не установлено";
                default: return "ответ не получен";
            }
        }

        private static string ErrorCode(Exception error) => error is SocketException socket
            ? "SOCKET=" + socket.NativeErrorCode : Finding.Hex(error.HResult);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProxyInfo
        {
            internal uint AccessType;
            internal IntPtr Proxy;
            internal IntPtr Bypass;
        }

        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpGetDefaultProxyConfiguration(out ProxyInfo info);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr pointer);
    }
}
