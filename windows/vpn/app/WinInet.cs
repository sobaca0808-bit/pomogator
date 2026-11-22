using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pomogator.Vpn;

internal sealed class WinInet : IProxySettings
{
    // INTERNET_OPTION_PER_CONNECTION_OPTION with a null connection applies to LAN only.
    private const uint PerConnectionOption = 75;
    private const uint Flags = 1, Server = 2, Bypass = 3, Pac = 4, FlagsUi = 10;

    private struct Option
    {
        internal uint Id;
        internal IntPtr Value;
    }

    private struct OptionList
    {
        internal uint Size;
        internal IntPtr Connection;
        internal uint Count;
        internal uint ErrorOption;
        internal IntPtr Options;
    }

    public ProxyState Read()
    {
        var options = new[] { new Option { Id = FlagsUi }, new Option { Id = Server },
            new Option { Id = Bypass }, new Option { Id = Pac } };
        var list = Build(options);
        try
        {
            var size = list.Size;
            if (!InternetQueryOption(IntPtr.Zero, PerConnectionOption, ref list, ref size)) Fail();
            for (var i = 0; i < options.Length; i++) options[i] = Marshal.PtrToStructure<Option>(At(list, i));
            return new ProxyState(unchecked((uint)options[0].Value.ToInt64()), Text(options[1]), Text(options[2]), Text(options[3]));
        }
        finally
        {
            for (var i = 1; i < options.Length; i++)
            {
                var pointer = Marshal.PtrToStructure<Option>(At(list, i)).Value;
                if (pointer != IntPtr.Zero) GlobalFree(pointer);
            }
            Marshal.FreeHGlobal(list.Options);
        }
    }

    public void Write(ProxyState state)
    {
        // Bypass/PAC values are observed to detect external changes. Only flags and server are changed.
        var server = Marshal.StringToHGlobalUni(state.Server);
        var list = Build(new[] {
            new Option { Id = Flags, Value = new IntPtr(state.Flags) },
            new Option { Id = Server, Value = server }
        });
        try
        {
            if (!InternetSetOption(IntPtr.Zero, PerConnectionOption, ref list, list.Size)) Fail();
            if (!Notify(IntPtr.Zero, 39, IntPtr.Zero, 0) || !Notify(IntPtr.Zero, 37, IntPtr.Zero, 0)) Fail();
        }
        finally
        {
            Marshal.FreeHGlobal(list.Options);
            Marshal.FreeHGlobal(server);
        }
    }

    private static OptionList Build(Option[] options)
    {
        var list = new OptionList { Size = (uint)Marshal.SizeOf<OptionList>(), Count = (uint)options.Length,
            Options = Marshal.AllocHGlobal(Marshal.SizeOf<Option>() * options.Length) };
        for (var i = 0; i < options.Length; i++) Marshal.StructureToPtr(options[i], At(list, i), false);
        return list;
    }

    private static IntPtr At(OptionList list, int index) => IntPtr.Add(list.Options, index * Marshal.SizeOf<Option>());
    private static string Text(Option option) => Marshal.PtrToStringUni(option.Value) ?? "";
    private static void Fail() => throw new Win32Exception(Marshal.GetLastWin32Error());

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetQueryOption(IntPtr internet, uint option, ref OptionList list, ref uint size);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr internet, uint option, ref OptionList list, uint size);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Notify(IntPtr internet, uint option, IntPtr buffer, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
