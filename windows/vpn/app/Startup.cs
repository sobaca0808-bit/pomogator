using Microsoft.Win32;

namespace Pomogator.Vpn;

internal static class Startup
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PomogatorVpn";
    private static string Command => "\"" + Environment.ProcessPath + "\" --tray";

    internal static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Run);
            return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Run);
            if (value) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
