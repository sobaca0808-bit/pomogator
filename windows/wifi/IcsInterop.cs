using System;
using System.Runtime.InteropServices;

namespace PomogatorWifi
{
    // Interface GUIDs, vtable order and parameter types follow Windows SDK netcon.h.
    [ComImport, Guid("C08956B7-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetSharingManager
    {
        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetSharingInstalled();
        void _VtblGap1_2();
        [PreserveSig]
        int GetConfiguration([MarshalAs(UnmanagedType.Interface)] INetConnection connection,
            [MarshalAs(UnmanagedType.Interface)] out INetSharingConfiguration configuration);
        INetSharingEveryConnectionCollection GetEveryConnection();
        INetConnectionProps GetConnectionProps([MarshalAs(UnmanagedType.Interface)] INetConnection connection);
    }

    [ComImport, Guid("33C4643C-7811-46FA-A89A-768597BD7223"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetSharingEveryConnectionCollection
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetNewEnum();
    }

    // Opaque IUnknown connection passed back to the manager. No connection mutation methods are imported.
    [ComImport, Guid("C08956A1-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface INetConnection { }

    [ComImport, Guid("F4277C95-CE5B-463D-8167-5662D9BCAA72"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetConnectionProps
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetGuid();
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();
        void _VtblGap1_1();
        int GetStatus();
        int GetMediaType();
        uint GetCharacteristics();
    }

    [ComImport, Guid("C08956B6-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetSharingConfiguration
    {
        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetSharingEnabled();
        SharingRole GetSharingConnectionType();
        void DisableSharing();
        void EnableSharing(SharingRole role);
    }
}
