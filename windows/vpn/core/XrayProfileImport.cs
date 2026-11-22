using System.Text.Json;

namespace Pomogator.Vpn;

internal static class XrayProfileImport
{
    internal static Profile Parse(string text, bool keepRouting)
    {
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 48 });
            var root = json.RootElement;
            var outers = root.GetProperty("outbounds").EnumerateArray().ToArray();
            var primary = outers.FirstOrDefault(item => Protocol(item) == "vless");
            if (primary.ValueKind == JsonValueKind.Undefined)
                throw new VpnError("В JSON Xray не найдено исходящее подключение VLESS.");
            var profile = ReadVless(primary);
            if (root.TryGetProperty("remarks", out var remarks) && remarks.ValueKind == JsonValueKind.String &&
                remarks.GetString() is { Length: > 0 and <= 100 } name && !name.Any(char.IsControl))
                profile = profile with { Name = name };
            return keepRouting ? profile with { Full = ReadFull(root, outers, primary) } : profile;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw Invalid();
        }
    }

    private static string Protocol(JsonElement item) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";

    private static Profile ReadVless(JsonElement outbound)
    {
        var settings = outbound.GetProperty("settings");
        var endpoint = settings.TryGetProperty("vnext", out var endpoints) ? Single(endpoints) : settings;
        var user = endpoint.TryGetProperty("users", out var users) ? Single(users) : endpoint;
        var stream = outbound.GetProperty("streamSettings");
        if (settings.TryGetProperty("vnext", out _))
        {
            RequireFields(settings, "vnext");
            RequireFields(endpoint, "address", "port", "users");
            RequireFields(user, "id", "encryption", "flow", "level", "email");
        }
        else RequireFields(user, "address", "port", "id", "encryption", "flow", "level", "email");

        var host = Text(endpoint, "address");
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw Invalid();
        if (Uri.CheckHostName(host) == UriHostNameType.IPv6) host = "[" + host + "]";
        var port = endpoint.GetProperty("port").GetInt32();
        var security = Text(stream, "security", "none");
        var transport = Text(stream, "network", "tcp");
        var values = new Dictionary<string, string> {
            ["security"] = security, ["type"] = transport,
            ["encryption"] = Text(user, "encryption", "none")
        };
        Add(values, "flow", user, "flow");
        if (stream.TryGetProperty("tlsSettings", out var tls))
        {
            if (security != "tls") throw Invalid();
            RequireFields(tls, "serverName", "fingerprint", "alpn", "allowInsecure");
            Add(values, "sni", tls, "serverName");
            Add(values, "fp", tls, "fingerprint");
            if (tls.TryGetProperty("alpn", out var alpn))
                values["alpn"] = string.Join(",", alpn.EnumerateArray().Select(item => item.GetString()));
            if (tls.TryGetProperty("allowInsecure", out var insecure) && insecure.GetBoolean())
                values["allowInsecure"] = "1";
        }
        if (stream.TryGetProperty("realitySettings", out var reality))
        {
            if (security != "reality") throw Invalid();
            RequireFields(reality, "serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "show");
            var publicKey = Text(reality, "publicKey");
            var password = Text(reality, "password");
            if (publicKey != "" && password != "" && publicKey != password) throw Invalid();
            Add(values, "sni", reality, "serverName");
            Add(values, "fp", reality, "fingerprint");
            values["pbk"] = password == "" ? publicKey : password;
            Add(values, "sid", reality, "shortId");
            if (Text(reality, "spiderX") != "") Add(values, "spx", reality, "spiderX");
        }

        foreach (var candidate in new[] { "raw", "tcp", "ws", "httpupgrade", "xhttp", "grpc" })
        {
            if (!stream.TryGetProperty(candidate + "Settings", out var transportSettings)) continue;
            if (candidate != transport && !(candidate is "raw" or "tcp" && transport is "raw" or "tcp"))
                continue;
            if (candidate is "raw" or "tcp")
            {
                RequireFields(transportSettings, "header", "acceptProxyProtocol");
                if (transportSettings.TryGetProperty("header", out var header))
                {
                    RequireFields(header, "type");
                    if (Text(header, "type", "none") != "none") throw Invalid();
                }
            }
            else if (candidate == "grpc")
            {
                RequireFields(transportSettings, "serviceName", "authority", "multiMode");
                Add(values, "serviceName", transportSettings, "serviceName");
                Add(values, "authority", transportSettings, "authority");
                if (transportSettings.TryGetProperty("multiMode", out var multi) && multi.GetBoolean()) values["mode"] = "multi";
            }
            else
            {
                RequireFields(transportSettings, "host", "path", "mode");
                Add(values, "host", transportSettings, "host");
                Add(values, "path", transportSettings, "path");
                Add(values, "mode", transportSettings, "mode");
            }
        }

        var query = string.Join("&", values.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        return Profiles.Parse($"vless://{Text(user, "id")}@{host}:{port}?{query}#{Uri.EscapeDataString(Text(outbound, "tag"))}");
    }

    private static FullRouting ReadFull(JsonElement root, JsonElement[] originals, JsonElement primary)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var dnsTagName = root.TryGetProperty("dns", out var configuredDns) ? Text(configuredDns, "tag") : "";
        if (dnsTagName != "" && !ValidTag(dnsTagName)) throw Unsupported("тег DNS несовместим");
        var outbounds = new List<JsonElement>();
        for (var index = 0; index < originals.Length; index++)
        {
            var outbound = originals[index];
            var protocol = Protocol(outbound);
            var tag = Text(outbound, "tag", originals.Length == 1 ? "vpn" : "");
            if (!ValidTag(tag) || !tags.Add(tag))
                throw Unsupported("исходящим подключениям нужны уникальные теги");
            if (protocol == "vless")
            {
                RequireFields(outbound, "tag", "protocol", "settings", "streamSettings", "mux");
                RequireFields(outbound.GetProperty("streamSettings"), "network", "security", "tlsSettings",
                    "realitySettings", "rawSettings", "tcpSettings", "wsSettings", "httpupgradeSettings", "xhttpSettings", "grpcSettings");
                if (outbound.TryGetProperty("mux", out var mux) && mux.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean())
                    throw Unsupported("включённый multiplexing не поддерживается");
                outbounds.Add(JsonSerializer.SerializeToElement(XrayConfig.Outbound(ReadVless(outbound), tag)));
            }
            else if (protocol is "freedom" or "blackhole" or "dns")
            {
                RequireFields(outbound, "tag", "protocol", "settings");
                if (outbound.TryGetProperty("settings", out var settings))
                {
                    if (protocol == "freedom") RequireFields(settings, "domainStrategy", "targetStrategy");
                    else if (protocol == "blackhole") RequireFields(settings, "response");
                    else RequireFields(settings, "network", "address", "port", "nonIPQuery", "blockTypes");
                    ValidateEmbedded(settings);
                }
                outbounds.Add(outbound.Clone());
            }
            else throw Unsupported("тип одного из дополнительных исходящих подключений не поддерживается");
        }

        string? http = null, socks = null;
        JsonElement? httpSniffing = null, socksSniffing = null;
        bool socksUdp = false;
        var inboundTags = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("inbounds", out var inbounds))
        {
            foreach (var input in inbounds.EnumerateArray())
            {
                var protocol = Protocol(input);
                var tag = Text(input, "tag");
                if (protocol is not ("http" or "socks") || !ValidTag(tag) || !inboundTags.Add(tag))
                    throw Unsupported("поддерживаются исходные теги локальных HTTP и SOCKS");
                if (input.TryGetProperty("settings", out var settings))
                {
                    if (Text(settings, "auth", "noauth") != "noauth" ||
                        settings.TryGetProperty("accounts", out var accounts) && accounts.GetArrayLength() != 0)
                        throw Unsupported("у исходного локального входа настроена аутентификация");
                    if (protocol == "socks" && settings.TryGetProperty("udp", out var udp)) socksUdp = udp.GetBoolean();
                }
                JsonElement? sniffing = null;
                if (input.TryGetProperty("sniffing", out var configured))
                {
                    RequireFields(configured, "enabled", "destOverride", "routeOnly", "metadataOnly", "domainsExcluded", "ipsExcluded");
                    ValidateEmbedded(configured);
                    sniffing = configured.Clone();
                }
                if (protocol == "http" && http == null) { http = tag; httpSniffing = sniffing; }
                else if (protocol == "socks" && socks == null) { socks = tag; socksSniffing = sniffing; }
                else throw Unsupported("допускается один вход HTTP и один SOCKS");
            }
        }
        http ??= "pomogator-http";
        if (socks == http) throw Unsupported("тег входа конфликтует с локальным HTTP");
        inboundTags.Add(http);
        if (dnsTagName != "" && !inboundTags.Add(dnsTagName)) throw Unsupported("тег DNS совпадает с локальным входом");

        var routing = root.TryGetProperty("routing", out var route) ? route.Clone() : JsonSerializer.SerializeToElement(new { });
        RequireFields(routing, "domainStrategy", "domainMatcher", "rules");
        ValidateEmbedded(routing);
        if (routing.TryGetProperty("rules", out var rules))
            foreach (var rule in rules.EnumerateArray())
            {
                RequireFields(rule, "type", "domain", "ip", "port", "sourcePort", "network", "source", "protocol",
                    "inboundTag", "outboundTag", "user", "attrs", "ruleTag");
                if (Text(rule, "type", "field") != "field" || !tags.Contains(Text(rule, "outboundTag")) ||
                    rule.TryGetProperty("inboundTag", out var inputs) && inputs.EnumerateArray().Any(tag => !inboundTags.Contains(tag.GetString() ?? "")))
                    throw Unsupported("правило ссылается на отсутствующий вход или выход");
            }

        JsonElement? dns = null;
        if (root.TryGetProperty("dns", out var dnsSettings))
        {
            RequireFields(dnsSettings, "servers", "hosts", "tag", "clientIp", "queryStrategy", "disableCache", "disableFallback",
                "disableFallbackIfMatch", "disableFallbackUnlessMatch", "useSystemHosts", "serveStale", "serveExpiredTTL", "enableParallelQuery");
            ValidateEmbedded(dnsSettings);
            dns = dnsSettings.Clone();
        }
        return new FullRouting(Text(primary, "tag", "vpn"), http, socks, httpSniffing, socksSniffing, dns, routing, outbounds.ToArray(), socksUdp);
    }

    private static bool ValidTag(string tag) => tag.Length is > 0 and <= 100 && !tag.Any(char.IsControl) && tag != XrayConfig.ProbeTag;

    private static void ValidateEmbedded(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw Invalid();
                    ValidateEmbedded(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray()) ValidateEmbedded(value);
                break;
            case JsonValueKind.String:
                var text = element.GetString()!;
                if (text.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("ext-ip:", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("ext-domain:", StringComparison.OrdinalIgnoreCase))
                    throw Unsupported("внешние файлы правил не загружаются");
                break;
        }
    }

    private static VpnError Unsupported(string reason) =>
        new("Полный профиль несовместим: " + reason + ". Можно повторить импорт и выбрать упрощённый профиль.");

    private static JsonElement Single(JsonElement array)
    {
        if (array.GetArrayLength() != 1) throw Invalid();
        return array[0];
    }

    private static string Text(JsonElement element, string field, string fallback = "") =>
        element.TryGetProperty(field, out var value) ? value.GetString() ?? fallback : fallback;

    private static void Add(Dictionary<string, string> values, string key, JsonElement element, string field)
    {
        if (element.TryGetProperty(field, out var value)) values.Add(key, value.GetString() ?? "");
    }

    private static void RequireFields(JsonElement element, params string[] accepted)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!accepted.Contains(field.Name) || !seen.Add(field.Name))
                throw Unsupported("обнаружено неизвестное или повторное поле");
    }

    private static VpnError Invalid() => new("Не удалось прочитать параметры клиентского профиля VLESS в JSON Xray.");

}


public sealed record FullRouting(string PrimaryTag, string HttpTag, string? SocksTag,
    JsonElement? HttpSniffing, JsonElement? SocksSniffing, JsonElement? Dns, JsonElement Routing, JsonElement[] Outbounds, bool SocksUdp);
