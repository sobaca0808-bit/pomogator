using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PomogatorNetworkDiagnostics
{
    internal static class Ics
    {
        internal static Finding Read()
        {
            var details = new List<string>();
            var codes = new List<string>();
            var references = new List<object>();
            bool? installed = null;
            int count = 0;
            var stage = "HNetCfg.HNetShare.Create";
            try
            {
                var raw = Track(references, Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.HNetShare", true)));
                stage = "QueryInterface(INetSharingManager)";
                var manager = (INetSharingManager)raw;
                try
                {
                    installed = manager.GetSharingInstalled();
                    details.Add("SharingInstalled: " + (installed.Value ? "да." : "нет."));
                }
                catch (Exception error) { RecordError(details, codes, "SharingInstalled", error); }

                stage = "EnumEveryConnection";
                var collection = Track(references, manager.GetEveryConnection());
                stage = "EnumEveryConnection._NewEnum";
                var rawEnumerator = Track(references, collection.GetNewEnum());
                stage = "QueryInterface(IEnumVARIANT)";
                var enumerator = (IEnumVARIANT)rawEnumerator;
                var value = new object[1];
                while (count < 128)
                {
                    stage = "IEnumVARIANT.Next #" + (count + 1);
                    value[0] = null;
                    var result = enumerator.Next(1, value, IntPtr.Zero);
                    if (result == 1) break; // S_FALSE: end of enumeration.
                    if (result != 0) throw new COMException("IEnumVARIANT.Next", result);
                    count++;
                    ReadConnection(references, manager, value[0], count, details, codes);
                }
                if (count == 128)
                {
                    details.Add("Достигнут лимит перечисления (128 подключений).");
                    codes.Add("LIMIT");
                }
            }
            catch (Exception error) { RecordError(details, codes, stage, error); }
            finally
            {
                for (int index = references.Count - 1; index >= 0; index--)
                    Marshal.ReleaseComObject(references[index]);
            }

            details.Add("Обработано подключений: " + count + ". Читаются настройки ICS, включая роли «источник» и «получатель».");
            details.Add("Ошибка чтения одного объекта COM не подтверждает сбой службы SharedAccess или потерю интернета.");
            var complete = installed == true && count > 0 && codes.Count == 0;
            return Finding.Create(complete ? Outcome.Ok : Outcome.Review, complete
                ? "Объекты ICS доступны для чтения."
                : "Чтение ICS вернуло ошибку, неполный список или признак недоступности.", details, codes);
        }

        internal static void ReadConnection(List<object> references, INetSharingManager manager, object raw, int index,
            List<string> details, List<string> codes)
        {
            var tag = " #" + index;
            INetConnection connection;
            try { connection = (INetConnection)Track(references, raw); }
            catch (Exception error) { RecordError(details, codes, "QueryInterface(INetConnection)" + tag, error); return; }

            var stage = "NetConnectionProps";
            try
            {
                var props = Track(references, manager.GetConnectionProps(connection));
                stage = "NetConnectionProps.MediaType";
                var media = props.GetMediaType();
                stage = "NetConnectionProps.Guid";
                var guid = Guid.Parse(props.GetGuid());
                stage = "NetConnectionProps.Status";
                var state = props.GetStatus();
                // RAS connection aliases can contain login names; only identify these by type and GUID.
                var name = media == 6 ? "PPPoE" : media == 4 || media == 5 || media == 9 ? "RAS/туннель" : "подключение";
                if (media == 3 || media == 8)
                {
                    stage = "NetConnectionProps.Name";
                    name = CleanText(props.GetName());
                }
                details.Add(tag.TrimStart() + " " + name + " | GUID " + guid + " | MediaType=" + media + " | Status=" + state);
            }
            catch (Exception error) { RecordError(details, codes, stage + tag, error); }

            stage = "INetSharingConfigurationForINetConnection";
            try
            {
                var config = Track(references, manager.GetConfiguration(connection));
                stage = "SharingEnabled";
                if (!config.GetSharingEnabled())
                {
                    details.Add(tag.TrimStart() + " ICS: не включён.");
                    return;
                }
                stage = "SharingConnectionType";
                var role = config.GetSharingConnectionType();
                details.Add(tag.TrimStart() + " ICS: " +
                    (role == 0 ? "источник (public)." : role == 1 ? "получатель (private)." : "роль Windows " + role + "."));
            }
            catch (Exception error) { RecordError(details, codes, stage + tag, error); }
        }

        private static string CleanText(string text) =>
            (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

        private static void RecordError(List<string> details, List<string> codes, string stage, Exception error)
        {
            var code = Finding.Hex(error.HResult);
            codes.Add(code);
            details.Add(stage + " (" + code + ")" +
                (error.HResult == unchecked((int)0x80004002) ? " — требуемый COM-интерфейс недоступен (E_NOINTERFACE)." : "."));
        }

        private static T Track<T>(List<object> references, T value) where T : class
        {
            if (value == null) throw new COMException("COM returned null.", unchecked((int)0x80004003));
            if (Marshal.IsComObject(value)) references.Add(value);
            return value;
        }

        // Read-only subset of Windows SDK netcon.h in native vtable order.
        [ComImport, Guid("C08956B7-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface INetSharingManager
        {
            [return: MarshalAs(UnmanagedType.VariantBool)] bool GetSharingInstalled();
            void _VtblGap1_2();
            INetSharingConfiguration GetConfiguration(INetConnection connection);
            INetSharingEveryConnectionCollection GetEveryConnection();
            INetConnectionProps GetConnectionProps(INetConnection connection);
        }

        [ComImport, Guid("33C4643C-7811-46FA-A89A-768597BD7223"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface INetSharingEveryConnectionCollection
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
        }

        [ComImport, Guid("C08956A1-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface INetConnection { }

        [ComImport, Guid("F4277C95-CE5B-463D-8167-5662D9BCAA72"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface INetConnectionProps
        {
            [return: MarshalAs(UnmanagedType.BStr)] string GetGuid();
            [return: MarshalAs(UnmanagedType.BStr)] string GetName();
            void _VtblGap1_1();
            int GetStatus();
            int GetMediaType();
        }

        [ComImport, Guid("C08956B6-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface INetSharingConfiguration
        {
            [return: MarshalAs(UnmanagedType.VariantBool)] bool GetSharingEnabled();
            int GetSharingConnectionType();
        }
    }
}
