using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace Pomogator.Vpn;

public sealed class HealthPolicy
{
    public static readonly TimeSpan Regular = TimeSpan.FromSeconds(30);
    public int ConsecutiveFailures { get; private set; }
    public TimeSpan? Observe(bool success)
    {
        ConsecutiveFailures = success ? 0 : Math.Min(3, ConsecutiveFailures + 1);
        return ConsecutiveFailures switch {
            0 => Regular, 1 => TimeSpan.FromSeconds(5), 2 => TimeSpan.FromSeconds(15), _ => null
        };
    }
}

public static class InternetHealth
{
    private static readonly (string Url, int Status)[] Targets = {
        ("https://www.gstatic.com/generate_204", 204),
        ("https://www.cloudflare.com/cdn-cgi/trace", 200),
        ("https://www.microsoft.com/favicon.ico", 200)
    };

    public static async Task<TimeSpan?> ProbeAsync(HttpClient client, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempts = Targets.Select(target => TryTarget(client, target.Url, target.Status, stop.Token)).ToList();
        while (attempts.Count > 0)
        {
            var completed = await Task.WhenAny(attempts);
            attempts.Remove(completed);
            if (await completed is { } time)
            {
                stop.Cancel();
                return time;
            }
        }
        token.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task<TimeSpan?> TryTarget(HttpClient client, string url, int status, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var measure = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return (int)response.StatusCode == status ? measure.Elapsed : null;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return null; }
    }

    public static async Task<string?> ExternalIpAsync(HttpClient client, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.GetAsync("https://api.ipify.org/", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[64];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, deadline.Token);
            var text = System.Text.Encoding.ASCII.GetString(buffer, 0, count).Trim();
            return count < buffer.Length && IPAddress.TryParse(text, out var ip) ? ip.ToString() : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException ||
            error is OperationCanceledException && !token.IsCancellationRequested) { return null; }
    }
}
