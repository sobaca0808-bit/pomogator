using System.Runtime.InteropServices;

namespace Pomogator.Vpn;

internal sealed class TunConsole : IDisposable
{
    private readonly Handler handler = _ => true;
    internal TunConsole()
    {
        FreeConsole();
        if (!AllocConsole() || !SetConsoleCtrlHandler(handler, true))
            throw new VpnError("Не удалось создать отдельную консоль для корректной остановки ядра TUN.");
        ShowWindow(GetConsoleWindow(), 0);
    }

    internal void Signal() => GenerateConsoleCtrlEvent(1, 0);

    public void Dispose()
    {
        SetConsoleCtrlHandler(handler, false);
        FreeConsole();
    }

    private delegate bool Handler(uint control);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint kind, uint processGroup);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
