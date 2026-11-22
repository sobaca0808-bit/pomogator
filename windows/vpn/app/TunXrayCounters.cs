using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Pomogator.Vpn;

internal sealed record TunXraySample(long InboundUplink, long InboundDownlink, long OutboundUplink, long OutboundDownlink);

internal static class TunXrayCounters
{
    private const int MaxOutput = 8192;
    private static readonly string[] Names =
    [
        "inbound>>>pomogator-tun>>>traffic>>>uplink",
        "inbound>>>pomogator-tun>>>traffic>>>downlink",
        "outbound>>>vpn>>>traffic>>>uplink",
        "outbound>>>vpn>>>traffic>>>downlink"
    ];

    internal static async Task<TunXraySample?> Read(string executable, int port, int processId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535 || processId <= 0 ||
            !WindowsLifetime.OwnsLoopback(port, processId)) return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "api", "statsquery", "-server=127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), "-timeout=2" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            if (!(started = process.Start())) return null;
            process.BeginErrorReadLine();
            var output = new char[MaxOutput + 1];
            var size = 0;
            while (size < output.Length)
            {
                var read = await process.StandardOutput.ReadAsync(output.AsMemory(size), deadline.Token).AsTask().WaitAsync(deadline.Token);
                if (read == 0) break;
                size += read;
            }
            if (size > MaxOutput) return null;
            await process.WaitForExitAsync(deadline.Token);
            return process.ExitCode == 0 && WindowsLifetime.OwnsLoopback(port, processId) ?
                Parse(new string(output, 0, size)) : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException) { return null; }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    internal static TunXraySample? Parse(string output)
    {
        if (output.Length > MaxOutput) return null;
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("stat", out var stats) || stats.ValueKind != JsonValueKind.Array) return null;
            var values = new long?[Names.Length];
            foreach (var entry in stats.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
                var index = Array.IndexOf(Names, name.GetString());
                if (index < 0) continue;
                if (values[index].HasValue) return null;
                // Xray serializes a registered zero counter without its protobuf value field.
                if (!entry.TryGetProperty("value", out var value)) { values[index] = 0; continue; }
                long count = 0;
                var parsed = value.ValueKind switch {
                    JsonValueKind.Number => value.TryGetInt64(out count),
                    JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out count),
                    _ => false
                };
                if (!parsed || count < 0) return null;
                values[index] = count;
            }
            return values.All(value => value.HasValue) ? new TunXraySample(
                values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value) : null;
        }
        catch (JsonException) { return null; }
    }
}
