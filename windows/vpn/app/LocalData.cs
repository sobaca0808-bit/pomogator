using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pomogator.Vpn;

internal static class LocalData
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "PomogatorVpn");
    internal static string Profiles => Path.Combine(Root, "profiles.dpapi");
    internal static string Recovery => Path.Combine(Root, "proxy-recovery.dpapi");
    internal static string TunRecovery => Path.Combine(Root, "tun-recovery.dpapi");

    internal static FileStream Lock()
    {
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var json = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(json); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    internal static void Save<T>(string path, T value)
    {
        var temp = path + ".new";
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(value);
            try
            {
                var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
                using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            finally { CryptographicOperations.ZeroMemory(json); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class SystemProxy
{
    private readonly WinInet settings = new();
    private ProxyRecovery? active;
    internal bool Active => active != null;
    internal bool HasRecovery => File.Exists(LocalData.Recovery);

    internal void Enable(int port)
    {
        if (active != null || HasRecovery)
            throw new VpnError("Сначала обработайте сохранённую запись восстановления прокси.");
        var recovery = ProxyControl.Prepare(settings, port);
        LocalData.Save(LocalData.Recovery, recovery);
        active = recovery;
        ProxyControl.Apply(settings, recovery);
    }

    internal bool ReleaseIfChanged()
    {
        if (active == null || settings.Read() == active.Applied) return false;
        active = null;
        File.Delete(LocalData.Recovery);
        return true;
    }

    internal RestoreResult Restore(bool fromDisk = false)
    {
        var record = active ?? (fromDisk ? LocalData.Read<ProxyRecovery>(LocalData.Recovery) : null);
        if (record == null) return RestoreResult.AlreadyRestored;
        var result = ProxyControl.Restore(settings, record);
        File.Delete(LocalData.Recovery);
        active = null;
        return result;
    }
}
