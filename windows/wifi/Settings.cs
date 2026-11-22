using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace PomogatorWifi
{
    public sealed class UserSettings
    {
        public string Ssid { get; set; } = "POMOGATOR";
        public string Password { get; set; } = "";
        public bool AutoLaunch { get; set; }
        public bool AutoWifi { get; set; }
        public bool CloseToTray { get; set; }
        public bool AutoRestore { get; set; }
    }

    public sealed class SharingLease
    {
        public Guid Owner { get; set; }
        public Guid Source { get; set; }
        public Guid Target { get; set; }
    }

    internal static class Local
    {
        private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PomogatorWifi");
        internal static string SettingsPath => Path.Combine(Root, "settings-v2.dpapi");
        internal static string LeasePath => Path.Combine(Root, "ics-lease.dpapi");
        internal static string LogPath => Path.Combine(Root, "diagnostic-v2.log");

        internal static T Load<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { return Wire.Decode<T>(bytes); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        internal static void Save<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = Wire.Encode(value);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        internal static void SetAutostart(bool enabled, string executable, string name = "PomogatorWifi")
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                if (enabled) key.SetValue(name, "\"" + executable + "\" --logon", RegistryValueKind.String);
                else key.DeleteValue(name, false);
        }

        internal static void Log(string stage, string safeDetails = null)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("O") + " " + stage +
                    (safeDetails == null ? "" : " " + safeDetails) + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void LogError(string stage, Exception error)
        {
            var operation = error is IcsFailure failure ? failure.Operation : error.GetType().Name;
            Log(stage, operation + string.Format(" HRESULT=0x{0:X8}", error.HResult));
        }
    }
}
