using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;

namespace Pomogator.Vpn;

internal sealed class TunSocketDiagnostic(IPAddress peer, int port, bool rawTransport)
{
    private const string RoutePrefix = "Hit route rule: [";
    private const string DialPrefix = "dialing TCP to tcp:";
    private static readonly Regex LogLine = new(
        @"\[(?:Info|Warning|Error|Debug)\]\s+(?:\[(?<id>[1-9]\d{0,9})\]\s+)?(?<source>[\w/]+): (?<message>.*)", RegexOptions.CultureInvariant);
    private const string Endpoint = @"(?:\[[\da-f:%]+\]|(?:\d{1,3}\.){3}\d{1,3}):\d{1,5}";
    private static readonly Regex SocketTarget = new(
        @"\b(?:dial|read|write) (?:tcp[46]?|udp[46]?) (?:(?:" + Endpoint + @")->)?(?<target>" + Endpoint + @")(?![\d.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NativeCode = new(
        @"\b(?:errno|winsock|socket error|wsa(?:error)?)\s*[:= ]\s*(?<code>100\d\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly int[] Winsock = [10013, 10022, 10024, 10035, 10048, 10049, 10050, 10051, 10052, 10053, 10054,
        10055, 10057, 10060, 10061, 10064, 10065];
    private static readonly (int Code, string Message)[] Localized = OperatingSystem.IsWindows() ?
        Winsock.Select(code => (Code: code, Message: new Win32Exception(code).Message.Trim().TrimEnd('.')))
            .Where(entry => entry.Message.Length > 12).GroupBy(entry => entry.Message, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray() : [];

    private readonly Dictionary<uint, Flow> flows = new();
    private readonly Dictionary<string, int> events = new();
    private readonly object gate = new();
    private bool capped;

    private sealed class Flow
    {
        internal string Route = "поток без метки";
        internal string? Target;
    }

    internal void Read(string line)
    {
        var match = LogLine.Match(line);
        if (!match.Success) return;
        var source = match.Groups["source"].Value;
        var message = match.Groups["message"].Value;
        // Some Xray handlers log an error that already includes the same package prefix.
        if (message.StartsWith(source + ": ", StringComparison.Ordinal)) message = message[(source.Length + 2)..];
        uint.TryParse(match.Groups["id"].ValueSpan, out var id);
        lock (gate)
        {
            if (source == "app/dispatcher" && message.StartsWith(RoutePrefix, StringComparison.Ordinal))
            {
                var route = message[RoutePrefix.Length..].Split(']', 2)[0] switch
                {
                    "pmt-control-vless" => "контроль через Xray",
                    "pmt-wintun-probe" => "Wintun к цели HTTPS",
                    "pmt-doh-vless" => "резолвер DoH через VLESS",
                    "pmt-tun-vless" => "другой поток Wintun",
                    _ => null
                };
                if (route == null) return;
                if (id != 0) GetFlow(id).Route = route;
                else if (route == "резолвер DoH через VLESS") Add(route + " → выбран маршрут, ID потока отсутствует");
                return;
            }

            if (source == "transport/internet/tcp" && message.StartsWith(DialPrefix, StringComparison.Ordinal))
            {
                if (id != 0) GetFlow(id).Target = Destination(message[DialPrefix.Length..]);
                return;
            }

            var flow = id != 0 ? flows.GetValueOrDefault(id) : null;
            var context = flow?.Route ?? "поток без метки";
            if (source == "proxy/vless/outbound" && message.StartsWith("tunneling request to ", StringComparison.Ordinal))
            {
                var via = message.LastIndexOf(" via ", StringComparison.Ordinal);
                var target = via < 0 ? flow?.Target : Destination(message[(via + 5)..]);
                Add(context + " → " + (target ?? "назначение не подтверждено") + " · внешний транспорт открыт");
                return;
            }

            if (source == "app/dns" && (message.StartsWith("failed to retrieve response for ", StringComparison.Ordinal) ||
                message.StartsWith("failed to handle DOH response for ", StringComparison.Ordinal)))
            {
                Add("резолвер DoH · ошибка HTTP/DNS без привязки к исходящему сокету");
                return;
            }

            if (source != "app/proxyman/outbound" || !message.StartsWith("failed to process outbound traffic", StringComparison.Ordinal)) return;
            var stage = FailureStage(message, rawTransport);
            if (stage == null) return;
            var targets = SocketTarget.Matches(message).Select(item => Destination(item.Groups["target"].Value)).Distinct().ToArray();
            var destination = targets.Length > 1 ? "несколько назначений" : targets.FirstOrDefault() ?? flow?.Target ?? "назначение не подтверждено";
            var errors = NativeCode.Matches(message).Select(item => int.Parse(item.Groups["code"].Value))
                .Concat(Localized.Where(entry => message.Contains(entry.Message, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Code))
                .Distinct().Order().ToArray();
            var result = errors.Length > 0 ? "WinSock " + string.Join(", ", errors) : "код WinSock не передан ядром";
            Add(context + " → " + destination + " · " + stage + " · " + result);
        }
    }

    internal static string? FailureStage(string message, bool rawTransport)
    {
        var tcp = message.Contains("transport/internet/tcp: failed to dial to dest", StringComparison.Ordinal);
        var tls = message.Contains("REALITY:", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("x509:", StringComparison.OrdinalIgnoreCase) || message.Contains("tls:", StringComparison.OrdinalIgnoreCase);
        if (tcp && tls) return "повторы Xray содержат ошибки TCP и TLS/REALITY";
        if (tcp)
            return message.Contains(": bind:", StringComparison.OrdinalIgnoreCase) ? "привязка исходящего сокета" :
                message.Contains(": connectex:", StringComparison.OrdinalIgnoreCase) ? "подключение TCP (ConnectEx)" : "открытие TCP-соединения";
        if (message.Contains("failed to decode response header", StringComparison.Ordinal) ||
            message.Contains("failed to transfer request payload", StringComparison.Ordinal) ||
            message.Contains("failed to transfer response payload", StringComparison.Ordinal))
            return "передача VLESS";
        if (tls) return "TLS/REALITY внешнего соединения";
        if (message.Contains("proxy/vless/outbound: failed to find an available destination", StringComparison.Ordinal))
        {
            if (rawTransport && (message.Contains("read tcp ", StringComparison.Ordinal) ||
                                  message.Contains("write tcp ", StringComparison.Ordinal)))
                return "ввод-вывод после TCP на этапе внешнего TLS/REALITY";
            return "установление транспорта VLESS · стадия не указана";
        }
        return null;
    }

    internal string[] TakeEvents()
    {
        lock (gate)
        {
            var result = events.Select(item => item.Key + " × " + item.Value).ToList();
            if (capped) result.Add("часть сочетаний событий опущена");
            events.Clear();
            capped = false;
            return result.ToArray();
        }
    }

    private Flow GetFlow(uint id)
    {
        if (flows.TryGetValue(id, out var value)) return value;
        if (flows.Count == 1024) flows.Remove(flows.Keys.First());
        return flows[id] = new Flow();
    }

    private string Destination(string literal) =>
        IPEndPoint.TryParse(literal, out var target) ? target.Address.Equals(peer) && target.Port == port ?
            "IP:порт VPN-сервера" : "другой IP:порт" : "назначение не подтверждено";

    private void Add(string label)
    {
        if (events.TryGetValue(label, out var count)) events[label] = count + 1;
        else if (events.Count < 32) events.Add(label, 1);
        else capped = true;
    }
}
