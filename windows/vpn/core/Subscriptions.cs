using System.Net;
using System.Net.Http;

namespace Pomogator.Vpn;

public static class Subscriptions
{
    public static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.IdnHost.Length != 0;

    // The supplied HTTP client has automatic redirects disabled.
    public static async Task<string> DownloadAsync(HttpClient http, Uri source, CancellationToken token)
    {
        if (!IsHttps(source)) throw new VpnError("Укажите URL HTTPS без имени пользователя, пароля и фрагмента.");
        var visited = new HashSet<Uri>();
        var uri = source;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (!visited.Add(uri)) throw new VpnError("Поставщик подписки вернул цикл перенаправлений.");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location == null || !Uri.TryCreate(uri, location, out var next) || !IsHttps(next) ||
                    !string.Equals(next.IdnHost, source.IdnHost, StringComparison.OrdinalIgnoreCase) || next.Port != source.Port)
                    throw new VpnError("Подписка перенаправляет на другой сайт или незащищённый адрес. Уточните URL у поставщика.");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var guidance = response.StatusCode switch {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => " Проверьте, действует ли ссылка подписки.",
                    HttpStatusCode.NotFound or HttpStatusCode.Gone => " Запросите актуальный адрес подписки.",
                    HttpStatusCode.TooManyRequests => " Повторите импорт позже.",
                    _ => ""
                };
                throw new VpnError("Поставщик подписки вернул HTTP " + (int)response.StatusCode + "." + guidance);
            }
            if (response.Content.Headers.ContentLength > Profiles.MaxImport)
                throw new VpnError("Список превышает 1 МБ.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await Profiles.ReadListAsync(stream, token);
        }
        throw new VpnError("Поставщик подписки вернул больше трёх перенаправлений.");
    }
}
