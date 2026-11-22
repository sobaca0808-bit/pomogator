using System.Text.Json;

namespace Pomogator.Vpn;

internal static class XrayProfileImport
{
    // Import profile values only. Executable rules, local listeners and additional outbound protocols are never loaded.
    internal static Profile Parse(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var outbound = Single(json.RootElement.GetProperty("outbounds"));
            var settings = outbound.GetProperty("settings");
            var endpoint = Single(settings.GetProperty("vnext"));
            var user = Single(endpoint.GetProperty("users"));
            var stream = outbound.GetProperty("streamSettings");
            RequireFields(outbound, "tag", "protocol", "settings", "streamSettings");
            RequireFields(settings, "vnext");
            RequireFields(endpoint, "address", "port", "users");
            RequireFields(user, "id", "encryption", "flow", "level", "email");
            RequireFields(stream, "network", "security", "tlsSettings", "realitySettings",
                "rawSettings", "tcpSettings", "wsSettings", "httpupgradeSettings", "xhttpSettings", "grpcSettings");
            if (Text(outbound, "protocol") != "vless") throw Invalid();

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
                if (Text(reality, "publicKey") != "" && Text(reality, "password") != "") throw Invalid();
                Add(values, "sni", reality, "serverName");
                Add(values, "fp", reality, "fingerprint");
                Add(values, "pbk", reality, reality.TryGetProperty("password", out _) ? "password" : "publicKey");
                Add(values, "sid", reality, "shortId");
                Add(values, "spx", reality, "spiderX");
            }

            foreach (var candidate in new[] { "raw", "tcp", "ws", "httpupgrade", "xhttp", "grpc" })
            {
                if (!stream.TryGetProperty(candidate + "Settings", out var transportSettings)) continue;
                if (candidate != transport) throw Invalid();
                if (candidate is "raw" or "tcp") RequireFields(transportSettings);
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
        catch (VpnError) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw Invalid();
        }
    }

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
            if (!accepted.Contains(field.Name) || !seen.Add(field.Name)) throw Invalid();
    }

    private static VpnError Invalid() => new("Поддерживается клиентский JSON Xray с одним исходящим профилем VLESS, одним сервером и одним пользователем, без дополнительных параметров ядра.");
}
