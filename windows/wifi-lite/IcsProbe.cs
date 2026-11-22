using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace PomogatorWifiLite
{
    internal sealed class IcsConnection
    {
        internal Guid Id;
        internal string Name;
        internal int Media, Status;
        internal int? Role; // 0 — public/upstream, 1 — private/downstream.
    }

    internal sealed class IcsProbe
    {
        private readonly HashSet<Guid> knownPppoe = new HashSet<Guid>();
        private Task<string> pending;

        // Call from the window thread. Native COM can lag, so keep at most one reader in flight.
        internal Task<string> BeginRead(Guid hosted)
        {
            if (pending != null && !pending.IsCompleted) return null;
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(Read(hosted)); }
                catch (Exception error) { completion.SetResult($"Чтение ICS прервано · HRESULT 0x{error.HResult:X8}."); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            pending = completion.Task;
            thread.Start();
            return pending;
        }

        private string Read(Guid hosted)
        {
            var references = new List<object>();
            string operation = "создание HNetCfg.HNetShare";
            try
            {
                var manager = (IShareManager)Keep(Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.HNetShare", true)));
                operation = "SharingInstalled";
                if (!manager.Installed()) return "Windows сообщает SharingInstalled=false. Проверьте службу SharedAccess.";
                operation = "EnumEveryConnection";
                var enumeration = (IEnumVARIANT)Keep(Keep(manager.Connections()).Enumerator());
                var found = new List<IcsConnection>();
                var seen = new HashSet<Guid>();
                var entry = new object[1];
                for (int index = 1; ; index++)
                {
                    operation = "IEnumVARIANT.Next #" + index;
                    entry[0] = null;
                    int next = enumeration.Next(1, entry, IntPtr.Zero);
                    if (next == 1) break;
                    if (next != 0) throw new COMException("Ошибка перечисления.", next);
                    operation = "NetConnectionProps #" + index;
                    var connection = (INetConnection)Keep(entry[0]);
                    var props = Keep(manager.Properties(connection));
                    if (!Guid.TryParse(props.Id(), out var id) || id == Guid.Empty || !seen.Add(id))
                        return "Windows вернула неоднозначный список подключений. Роли ICS не подтверждены.";
                    var media = props.Media();
                    var state = props.Status();
                    var flags = props.Flags();
                    operation = "INetSharingConfigurationForINetConnection #" + index;
                    IShareConfiguration config = null;
                    int hr;
                    try { hr = manager.Configuration(connection, out config); }
                    catch (Exception error) when (error is InvalidCastException || error is COMException) { hr = error.HResult; }
                    if (hr != 0)
                    {
                        // Some Windows installations enumerate a disconnected object without any usable network medium.
                        if (hr == unchecked((int)0x80004002) && media == 0 && state == 0 && id != hosted &&
                            (flags & 0x53100u) == 0) continue;
                        throw new COMException("Не прочитана конфигурация ICS.", hr);
                    }
                    Keep(config);
                    operation = "SharingEnabled #" + index;
                    int? role = null;
                    if (config.Enabled())
                    {
                        operation = "SharingConnectionType #" + index;
                        role = config.Role();
                        if (role != 0 && role != 1) return "Windows вернула неизвестную роль ICS.";
                    }
                    operation = "NetConnectionProps.Name #" + index;
                    found.Add(new IcsConnection { Id = id, Name = props.Name(), Media = media, Status = state, Role = role });
                }
                return Summarize(found, hosted, knownPppoe);
            }
            catch (Exception error)
            {
                return $"{operation} · HRESULT 0x{error.HResult:X8}. Проверка ICS не завершена.";
            }
            finally
            {
                for (var index = references.Count - 1; index >= 0; index--)
                    if (Marshal.IsComObject(references[index])) Marshal.ReleaseComObject(references[index]);
            }

            T Keep<T>(T value) where T : class
            {
                if (value == null) throw new COMException("Пустой COM-интерфейс.", unchecked((int)0x80004003));
                references.Add(value);
                return value;
            }
        }

        internal static string Summarize(IList<IcsConnection> all, Guid hosted, HashSet<Guid> knownPppoe)
        {
            foreach (var item in all.Where(connection => connection.Media == 6)) knownPppoe.Add(item.Id);
            var pppoe = all.Where(item => item.Status == 2 && (item.Media == 6 || item.Media == 9 && knownPppoe.Contains(item.Id))).ToArray();
            var destination = all.SingleOrDefault(item => item.Id == hosted && (item.Media == 3 || item.Media == 8));
            var upstream = pppoe.Length == 1 ? pppoe[0] : null;
            var names = upstream == null ? "" : "\r\nPPPoE: " + upstream.Name;
            if (destination != null) names += "\r\nАдаптер точки: " + destination.Name;
            if (hosted == Guid.Empty) return "Виртуальный адаптер определяется после запуска точки." + names;
            if (upstream == null)
            {
                if (pppoe.Length > 1) return "Активно несколько PPPoE. Выберите нужное подключение в Windows." + names;
                if (all.Any(item => item.Media == 9 && item.Status == 2))
                    return "Windows показывает общий WAN. Тип PPPoE для этого подключения не подтверждён." + names;
                return "Активное PPPoE не найдено по данным ICS." + names;
            }
            if (destination == null) return "ICS пока не показывает виртуальный адаптер Hosted Network." + names;
            var roles = all.Count(item => item.Role.HasValue);
            if (roles == 2 && upstream.Role == 0 && destination.Role == 1)
                return "Роли ICS подтверждены: PPPoE → виртуальный Wi-Fi. Интернет проверьте на телефоне." + names;
            return (roles == 0 ? "ICS выключен. Настройте общий доступ вручную в ncpa.cpl." :
                "Роли ICS настроены для другой пары или частично. Проверьте вкладку «Доступ» в ncpa.cpl.") + names;
        }
    }

    // Windows SDK netcon.h. Interfaces are intentionally read-only.
    [ComImport, Guid("C08956B7-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IShareManager
    {
        [return: MarshalAs(UnmanagedType.VariantBool)] bool Installed();
        void _VtblGap1_2();
        [PreserveSig] int Configuration([MarshalAs(UnmanagedType.Interface)] INetConnection connection,
            [MarshalAs(UnmanagedType.Interface)] out IShareConfiguration config);
        IConnectionCollection Connections();
        IConnectionProperties Properties([MarshalAs(UnmanagedType.Interface)] INetConnection connection);
    }

    [ComImport, Guid("C08956A1-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface INetConnection { }

    [ComImport, Guid("33C4643C-7811-46FA-A89A-768597BD7223"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IConnectionCollection { [return: MarshalAs(UnmanagedType.IUnknown)] object Enumerator(); }

    [ComImport, Guid("F4277C95-CE5B-463D-8167-5662D9BCAA72"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IConnectionProperties
    {
        [return: MarshalAs(UnmanagedType.BStr)] string Id();
        [return: MarshalAs(UnmanagedType.BStr)] string Name();
        void _VtblGap1_1();
        int Status();
        int Media();
        uint Flags();
    }

    [ComImport, Guid("C08956B6-1CD3-11D1-B1C5-00805FC1270E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IShareConfiguration
    {
        [return: MarshalAs(UnmanagedType.VariantBool)] bool Enabled();
        int Role();
    }
}
