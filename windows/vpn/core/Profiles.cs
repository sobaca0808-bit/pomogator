using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pomogator.Vpn;

public sealed class VpnError(string message, Check? failedCheck = null) : Exception(message)
{
    public Check? FailedCheck { get; } = failedCheck;
}

public sealed record Profile(string Name, string Host, int Port, Guid Id, string Security, string Transport,
    IReadOnlyDictionary<string, string> Options, FullRouting? Full = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Option("sid") is { Length: > 0 } shortId &&
        Name.Contains(shortId, StringComparison.OrdinalIgnoreCase)
            ? Host + ":" + Port : Profiles.SafeName(Name, Host + ":" + Port);

    public string Option(string name, string fallback = "") => Options.TryGetValue(name, out var value) ? value : fallback;
}

public static class Profiles
{
    public const int MaxImport = 1024 * 1024;
    public const int MaxProfiles = 100;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase) {
        "encryption", "security", "type", "flow", "sni", "fp", "pbk", "sid", "spx",
        "alpn", "host", "path", "mode", "serviceName", "authority", "headerType", "allowInsecure", "insecure"
    };
    private static readonly HashSet<string> Fingerprints = new(StringComparer.Ordinal) {
        "chrome", "firefox", "safari", "ios", "android", "edge", "360", "qq", "random", "randomized"
    };

    public static string SafeName(string name, string fallback) =>
        name.Length == 0 || name.Contains("://", StringComparison.Ordinal) ||
        Regex.IsMatch(name, @"[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}|[A-Za-z0-9_-]{32,}", RegexOptions.IgnoreCase)
            ? fallback : name;

    public static Profile Parse(string link)
    {
        if (link.Length > 8192 || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("vless", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(uri.UserInfo, "D", out var id) || id == Guid.Empty ||
            uri.HostNameType == UriHostNameType.Unknown || uri.Port is < 1 or > 65535 ||
            uri.AbsolutePath is not ("" or "/"))
            throw new VpnError("Нужна ссылка vless:// с UUID, адресом и портом сервера.");

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = field.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
            if (!Common.TryGetValue(key, out var canonical)) throw new VpnError("Ссылка содержит параметр, который не поддерживается в этой версии.");
            if (value.Any(char.IsControl) || !options.TryAdd(canonical, value))
                throw new VpnError("Ссылка содержит повторяющиеся параметры или управляющие символы.");
        }

        var security = Get(options, "security", "none").ToLowerInvariant();
        var transport = Get(options, "type", "tcp").ToLowerInvariant() switch {
            "tcp" => "raw", "splithttp" => "xhttp", var other => other
        };
        var host = uri.IdnHost.Trim('[', ']');
        if (security is not ("none" or "tls" or "reality") ||
            transport is not ("raw" or "ws" or "grpc" or "httpupgrade" or "xhttp"))
            throw new VpnError("В этой версии доступны TLS, REALITY и транспорты TCP, WebSocket, gRPC, HTTPUpgrade, XHTTP.");
        if (security == "none" && !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))))
            throw new VpnError("Профиль без TLS или REALITY разрешён только для локального тестового сервера.");
        if (Get(options, "encryption", "none") != "none" ||
            Get(options, "allowInsecure", "0") is not ("0" or "false") ||
            Get(options, "insecure", "0") is not ("0" or "false") ||
            Get(options, "headerType", "none") != "none")
            throw new VpnError("Профиль требует неподдерживаемого шифрования, заголовков TCP или отключения проверки сертификата.");

        var flow = Get(options, "flow", "");
        if (flow != "" && (flow != "xtls-rprx-vision" || transport != "raw" || security == "none"))
            throw new VpnError("Режим Vision требует TCP и TLS или REALITY.");
        var fp = Get(options, "fp", security == "reality" ? "chrome" : "");
        if (fp != "" && !Fingerprints.Contains(fp))
            throw new VpnError("Этот fingerprint не поддерживается.");
        var sni = Get(options, "sni", "");
        if (sni != "" && Uri.CheckHostName(sni) != UriHostNameType.Dns)
            throw new VpnError("Укажите корректное доменное имя SNI.");
        var alpn = Get(options, "alpn", "");
        if (alpn != "" && alpn.Split(',').Any(x => x is not ("h2" or "http/1.1" or "h3")))
            throw new VpnError("Укажите поддерживаемые значения ALPN.");
        if (security == "reality")
        {
            if (transport is not ("raw" or "xhttp" or "grpc") || sni == "" ||
                !KeyLength(Get(options, "pbk", "")) || !ValidShortId(Get(options, "sid", "")) || fp == "" || alpn != "")
                throw new VpnError("Проверьте SNI, публичный ключ, Short ID, fingerprint и транспорт REALITY.");
        }
        else if (options.ContainsKey("pbk") || options.ContainsKey("sid") || options.ContainsKey("spx"))
            throw new VpnError("Параметры публичного ключа и Short ID применяются только к REALITY.");
        if (security == "none" && (sni != "" || fp != "" || alpn != ""))
            throw new VpnError("Параметры SNI, fingerprint и ALPN требуют защищённого профиля.");

        if (options.ContainsKey("path") && transport is not ("ws" or "httpupgrade" or "xhttp") ||
            options.ContainsKey("host") && transport is not ("ws" or "httpupgrade" or "xhttp") ||
            (options.ContainsKey("serviceName") || options.ContainsKey("authority")) && transport != "grpc" ||
            options.ContainsKey("mode") && transport is not ("grpc" or "xhttp"))
            throw new VpnError("Часть параметров ссылки не соответствует выбранному транспорту.");
        var mode = Get(options, "mode", "");
        if ((transport == "grpc" && mode is not ("" or "gun" or "multi")) ||
            (transport == "xhttp" && mode is not ("" or "auto" or "packet-up" or "stream-up" or "stream-one")))
            throw new VpnError("Этот режим транспорта не поддерживается.");
        if (!Get(options, "path", "/").StartsWith('/') ||
            !Get(options, "spx", "/").StartsWith('/'))
            throw new VpnError("Путь должен начинаться с символа /.");

        if (security == "none" && host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
        var alias = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')).Trim();
        if (alias.Any(char.IsControl) || alias.Length > 100) throw new VpnError("Название профиля слишком длинное или содержит управляющие символы.");
        return new Profile(alias == "" ? host + ":" + uri.Port : alias, host, uri.Port, id, security, transport, options);
    }

    public static bool IsXrayJson(string text) => text.Trim().TrimStart('\uFEFF').TrimStart().StartsWith('{');

    public static IReadOnlyList<Profile> ParseList(string text, bool keepRouting = false)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxImport) throw new VpnError("Список превышает 1 МБ.");
        text = text.Trim().TrimStart('\uFEFF').Trim();
        if (text.StartsWith('{')) return new[] { XrayProfileImport.Parse(text, keepRouting) };
        if (!text.Contains("vless://", StringComparison.OrdinalIgnoreCase))
        {
            try { text = new UTF8Encoding(false, true).GetString(DecodeBase64(text)).Trim().TrimStart('\uFEFF'); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException)
            { throw new VpnError("Поддерживаются списки ссылок vless:// в UTF‑8 или Base64."); }
        }
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')).ToArray();
        if (lines.Length is 0 or > MaxProfiles) throw new VpnError("Список должен содержать от 1 до 100 ссылок VLESS.");
        var profiles = new List<Profile>();
        for (var index = 0; index < lines.Length; index++)
        {
            try { profiles.Add(Parse(lines[index])); }
            catch (VpnError error) { throw new VpnError($"Строка {index + 1}. {error.Message}"); }
        }
        return profiles;
    }

    public static async Task<string> ReadListAsync(Stream source, CancellationToken token)
    {
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int bytes;
        while ((bytes = await source.ReadAsync(buffer, token)) != 0)
        {
            if (bounded.Length + bytes > MaxImport) throw new VpnError("Список превышает 1 МБ.");
            bounded.Write(buffer, 0, bytes);
        }
        try { return new UTF8Encoding(false, true).GetString(bounded.ToArray()); }
        catch (DecoderFallbackException) { throw new VpnError("Сохраните файл в кодировке UTF‑8."); }
    }

    private static byte[] DecodeBase64(string value)
    {
        var standard = Regex.Replace(value, @"\s+", "").Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard.PadRight((standard.Length + 3) / 4 * 4, '='));
    }

    private static bool KeyLength(string key)
    {
        try { return Regex.IsMatch(key, @"\A[A-Za-z0-9_-]{43}=?\z") && DecodeBase64(key).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool ValidShortId(string id) => id.Length <= 16 && id.Length % 2 == 0 && id.All(Uri.IsHexDigit);
    private static string Get(Dictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var value) ? value : fallback;
}

public static class XrayConfig
{
    internal const string ProbeTag = "pomogator-vpn-check";

    public static string Create(Profile profile, int localPort, int? probePort = null, int? socksPort = null)
    {
        var inbound = Local("http", profile.Full?.HttpTag ?? "pomogator-http", localPort, profile.Full?.HttpSniffing);
        if (profile.Full is not { } full)
            return JsonSerializer.Serialize(new {
                log = new { loglevel = "none" }, inbounds = new[] { inbound }, outbounds = new[] { Outbound(profile, "vpn") }
            });
        if (probePort is null || (full.SocksTag != null && socksPort is null))
            throw new VpnError("Не выделены локальные порты для полного профиля.");
        var inbounds = new List<object> { inbound, Local("http", ProbeTag, probePort.Value, null) };
        if (full.SocksTag != null) inbounds.Add(Local("socks", full.SocksTag, socksPort!.Value, full.SocksSniffing, full.SocksUdp));

        var routing = new Dictionary<string, object>();
        foreach (var property in full.Routing.EnumerateObject())
            if (property.Name != "rules") routing[property.Name] = property.Value;
        // A separate loopback listener proves the selected VLESS, even when the full profile normally routes this domain directly.
        var rules = new List<object> { new { type = "field", inboundTag = new[] { ProbeTag }, outboundTag = full.PrimaryTag } };
        if (full.Routing.TryGetProperty("rules", out var imported))
            rules.AddRange(imported.EnumerateArray().Select(rule => (object)rule));
        routing["rules"] = rules;

        var config = new Dictionary<string, object> {
            ["log"] = new { loglevel = "none" }, ["inbounds"] = inbounds,
            ["outbounds"] = full.Outbounds, ["routing"] = routing
        };
        if (full.Dns is { } dns) config["dns"] = dns;
        return JsonSerializer.Serialize(config);
    }

    private static object Local(string protocol, string tag, int port, JsonElement? sniffing, bool udp = false)
    {
        var inbound = new Dictionary<string, object> { ["listen"] = "127.0.0.1", ["tag"] = tag, ["port"] = port, ["protocol"] = protocol };
        if (protocol == "socks") inbound["settings"] = new { auth = "noauth", udp, ip = "127.0.0.1" };
        if (sniffing is { } value) inbound["sniffing"] = value;
        return inbound;
    }

    internal static Dictionary<string, object> Outbound(Profile profile, string tag)
    {
        var stream = new Dictionary<string, object> {
            ["network"] = profile.Transport, ["security"] = profile.Security
        };
        if (profile.Security == "tls")
        {
            var tls = new Dictionary<string, object> { ["serverName"] = profile.Option("sni", profile.Host) };
            if (profile.Option("fp") != "") tls["fingerprint"] = profile.Option("fp");
            if (profile.Option("alpn") != "") tls["alpn"] = profile.Option("alpn").Split(',');
            stream["tlsSettings"] = tls;
        }
        if (profile.Security == "reality")
        {
            stream["realitySettings"] = new {
                serverName = profile.Option("sni"), fingerprint = profile.Option("fp", "chrome"),
                password = profile.Option("pbk"), shortId = profile.Option("sid"), spiderX = profile.Option("spx", "/")
            };
        }

        switch (profile.Transport)
        {
            case "ws":
            case "httpupgrade":
            case "xhttp":
                var settings = new Dictionary<string, object> { ["path"] = profile.Option("path", "/") };
                if (profile.Option("host") != "") settings["host"] = profile.Option("host");
                if (profile.Transport == "xhttp") settings["mode"] = profile.Option("mode", "auto");
                stream[profile.Transport + "Settings"] = settings;
                break;
            case "grpc":
                stream["grpcSettings"] = new {
                    serviceName = profile.Option("serviceName"), authority = profile.Option("authority"),
                    multiMode = profile.Option("mode") == "multi"
                };
                break;
        }
        return new Dictionary<string, object> {
            ["tag"] = tag, ["protocol"] = "vless", ["settings"] = new {
                vnext = new[] { new { address = profile.Host, port = profile.Port, users = new[] {
                    new { id = profile.Id.ToString("D"), encryption = "none", flow = profile.Option("flow") }
                } } }
            },
            ["streamSettings"] = stream
        };
    }
}
