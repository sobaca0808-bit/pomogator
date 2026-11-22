using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PomogatorWifi
{
    internal sealed class IcsFailure : Exception
    {
        internal string Operation { get; }
        internal int? NativeCode { get; }
        internal string Diagnostic => NativeCode is int code ? Operation + string.Format(" HRESULT=0x{0:X8}", code) :
            Operation + (InnerException == null ? "=false" : " managed=" + InnerException.GetType().Name);

        internal IcsFailure() : base("Windows сообщила, что ICS недоступен (SharingInstalled=false). " +
            "Проверьте службу «Общий доступ к подключению к Интернету» (SharedAccess) и вкладку «Доступ» в ncpa.cpl.")
        {
            Operation = "SharingInstalled";
        }

        internal IcsFailure(string operation, Exception cause)
            : base("Не удалось проверить или настроить ICS. Операция " + operation +
                   (NativeResult(cause) is int code ? string.Format(" (HRESULT 0x{0:X8}).", code) : "."), cause)
        {
            Operation = operation;
            NativeCode = NativeResult(cause);
            if (NativeCode.HasValue) HResult = NativeCode.Value;
        }

        private static int? NativeResult(Exception cause) => cause is ExternalException || cause is InvalidCastException
            ? (int?)cause.HResult : null;
    }

    internal sealed class IcsIssue
    {
        internal readonly Guid Id;
        internal readonly bool Unsupported;
        internal readonly string Detail;
        internal readonly bool ActiveWifiDirect;

        internal IcsIssue(IcsFailure error, Guid id, Connection connection, bool unsupported)
        {
            Id = id;
            Unsupported = unsupported;
            ActiveWifiDirect = connection != null && connection.Connected &&
                (connection.Flags & Characteristics.WifiDirect) != 0;
            Detail = error.Diagnostic + " GUID=" + (id == Guid.Empty ? "не прочитан" : id.ToString());
            if (connection != null)
                Detail += string.Format(" MediaType={0} Status={1} Characteristics=0x{2:X}",
                    (int)connection.Media, (int)connection.Status, (uint)connection.Flags);
        }
    }

    internal sealed class IcsSnapshot : IDisposable
    {
        private const int NoInterface = unchecked((int)0x80004002);
        private const string ReadConfiguration = "INetSharingConfigurationForINetConnection";
        private readonly List<object> comObjects = new List<object>();
        private readonly Dictionary<Guid, INetSharingConfiguration> configs = new Dictionary<Guid, INetSharingConfiguration>();
        internal readonly List<Connection> Connections = new List<Connection>();
        internal readonly List<IcsIssue> Issues = new List<IcsIssue>();

        internal IcsSnapshot(INetSharingManager manager = null)
        {
            string step = "HNetCfg.HNetShare.Create";
            try
            {
                if (manager == null)
                {
                    var raw = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.HNetShare", true)));
                    step = "QueryInterface(INetSharingManager)";
                    manager = (INetSharingManager)raw;
                }
                step = "SharingInstalled";
                if (!manager.GetSharingInstalled())
                    throw new IcsFailure();

                step = "EnumEveryConnection";
                var collection = Keep(manager.GetEveryConnection());
                step = "EnumEveryConnection._NewEnum";
                var rawEnumerator = Keep(collection.GetNewEnum());
                step = "QueryInterface(IEnumVARIANT)";
                var enumerator = (IEnumVARIANT)rawEnumerator;
                var item = new object[1];
                var seen = new HashSet<Guid>();
                for (var index = 1; ; index++)
                {
                    step = "IEnumVARIANT.Next #" + index;
                    item[0] = null;
                    if (!IcsEnumeration.HasItem(enumerator.Next(1, item, IntPtr.Zero))) break;
                    ReadConnection(manager, item[0], index, seen);
                }
            }
            catch (IcsFailure)
            {
                Dispose();
                throw;
            }
            catch (Exception cause)
            {
                Dispose();
                throw new IcsFailure(step, cause);
            }
        }

        private void ReadConnection(INetSharingManager manager, object item, int index, HashSet<Guid> seen)
        {
            var step = "QueryInterface(INetConnection)";
            var id = Guid.Empty;
            Connection connection = null;
            try
            {
                var raw = (INetConnection)Keep(item);
                step = "NetConnectionProps";
                var props = Keep(manager.GetConnectionProps(raw));
                step = "NetConnectionProps.Guid";
                if (!Guid.TryParse(props.GetGuid(), out id) || id == Guid.Empty || !seen.Add(id))
                    throw new WifiError("Windows вернула неоднозначный список подключений.");

                step = "NetConnectionProps.MediaType";
                var media = (MediaType)props.GetMediaType();
                step = "NetConnectionProps.Status";
                var status = (ConnectionStatus)props.GetStatus();
                step = "NetConnectionProps.Characteristics";
                var flags = (Characteristics)props.GetCharacteristics();
                connection = new Connection { Id = id, Media = media, Status = status, Flags = flags };

                step = ReadConfiguration;
                INetSharingConfiguration rawConfig;
                var result = manager.GetConfiguration(raw, out rawConfig);
                if (result != 0)
                    throw new COMException("Windows не вернула конфигурацию ICS.", result);
                var config = Keep(rawConfig);
                step = "SharingEnabled";
                if (config.GetSharingEnabled())
                {
                    step = "SharingConnectionType";
                    var role = config.GetSharingConnectionType();
                    if (role != SharingRole.Public && role != SharingRole.Private)
                        throw new WifiError("Windows вернула неизвестную роль ICS.");
                    connection.Sharing = role;
                }
                step = "NetConnectionProps.Name";
                connection.Name = props.GetName();
                configs.Add(id, config);
                Connections.Add(connection);
            }
            catch (Exception cause)
            {
                var failure = new IcsFailure(step + " #" + index, cause);
                // The call or marshaling its returned interface can yield E_NOINTERFACE.
                var unsupported = step == ReadConfiguration && failure.NativeCode == NoInterface &&
                    connection != null && connection.Media == MediaType.None && connection.Status == ConnectionStatus.Disconnected &&
                    (connection.Flags & (Characteristics.Shared | Characteristics.SharedPrivate | Characteristics.HomeNetCapable |
                                         Characteristics.HostedNetwork | Characteristics.WifiDirect)) == 0;
                Issues.Add(new IcsIssue(failure, id, connection, unsupported));
            }
        }

        internal void RequireVerified(Guid source, Guid target)
        {
            var blocker = Issues.FirstOrDefault(issue => !issue.Unsupported || issue.Id == source || issue.Id == target);
            if (blocker != null)
                throw new WifiError("Состояние ICS не подтверждено. " + blocker.Detail);
        }

        internal void Enable(Guid id, SharingRole role)
        {
            try { configs[id].EnableSharing(role); }
            catch (Exception cause) { throw new IcsFailure("EnableSharing(" + role + ") GUID=" + id, cause); }
        }

        internal void Disable(Guid id)
        {
            try { configs[id].DisableSharing(); }
            catch (Exception cause) { throw new IcsFailure("DisableSharing GUID=" + id, cause); }
        }

        internal bool IsSharing(Guid source, Guid target)
        {
            RequireVerified(source, target);
            return Policy.ConfirmsSharing(Connections, source, target);
        }

        private T Keep<T>(T value) where T : class
        {
            if (value == null) throw new COMException("Windows вернула пустой COM-объект.", unchecked((int)0x80004003));
            if (Marshal.IsComObject(value)) comObjects.Add(value);
            return value;
        }

        public void Dispose()
        {
            for (int index = comObjects.Count - 1; index >= 0; index--)
                Marshal.ReleaseComObject(comObjects[index]);
            comObjects.Clear();
        }
    }

    internal static class IcsEnumeration
    {
        internal static bool HasItem(int result)
        {
            if (result == 0) return true;  // S_OK
            if (result == 1) return false; // S_FALSE: no more entries
            throw new COMException("Сбой перечисления подключений ICS.", result);
        }
    }
}
