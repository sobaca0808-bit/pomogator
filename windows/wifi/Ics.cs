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

        internal IcsFailure(string operation, Exception cause)
            : base("Не удалось проверить или настроить ICS: " + operation +
                   string.Format(" (0x{0:X8}).", cause.HResult), cause)
        {
            Operation = operation;
            HResult = cause.HResult;
        }
    }

    internal sealed class IcsSnapshot : IDisposable
    {
        private readonly List<object> comObjects = new List<object>();
        private readonly Dictionary<Guid, INetSharingConfiguration> configs = new Dictionary<Guid, INetSharingConfiguration>();
        internal readonly List<Connection> Connections = new List<Connection>();

        internal IcsSnapshot()
        {
            string step = "HNetCfg.HNetShare.Create";
            try
            {
                var raw = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.HNetShare", true)));
                step = "QueryInterface(INetSharingManager)";
                var manager = (INetSharingManager)raw;
                step = "SharingInstalled";
                if (!manager.GetSharingInstalled())
                    throw new WifiError("Windows сообщает, что общий доступ ICS недоступен.");

                step = "EnumEveryConnection";
                var collection = Keep(manager.GetEveryConnection());
                step = "EnumEveryConnection._NewEnum";
                var rawEnumerator = Keep(collection.GetNewEnum());
                step = "QueryInterface(IEnumVARIANT)";
                var enumerator = (IEnumVARIANT)rawEnumerator;
                var item = new object[1];
                while (true)
                {
                    var index = Connections.Count + 1;
                    step = "IEnumVARIANT.Next #" + index;
                    var result = enumerator.Next(1, item, IntPtr.Zero);
                    if (!IcsEnumeration.HasItem(result)) break;
                    var rawConnection = Keep(item[0]);
                    step = "QueryInterface(INetConnection) #" + index;
                    var connection = (INetConnection)rawConnection;
                    step = "NetConnectionProps #" + index;
                    var props = Keep(manager.GetConnectionProps(connection));
                    step = "NetConnectionProps.Guid #" + index;
                    Guid id;
                    if (!Guid.TryParse(props.GetGuid(), out id) || id == Guid.Empty || configs.ContainsKey(id))
                        throw new WifiError("Windows вернула неоднозначный список подключений. Проверьте адаптеры в ncpa.cpl.");
                    step = "NetConnectionProps.Name #" + index;
                    var name = props.GetName();
                    step = "NetConnectionProps.MediaType #" + index;
                    var pppoe = props.GetMediaType() == 6; // NCM_PPPOE
                    step = "NetConnectionProps.Status #" + index;
                    var connected = props.GetStatus() == 2; // NCS_CONNECTED
                    step = "INetSharingConfigurationForINetConnection #" + index;
                    var config = Keep(manager.GetConfiguration(connection));
                    step = "SharingEnabled #" + index;
                    SharingRole? role = null;
                    if (config.GetSharingEnabled())
                    {
                        step = "SharingConnectionType #" + index;
                        role = config.GetSharingConnectionType();
                    }
                    configs.Add(id, config);
                    Connections.Add(new Connection { Id = id, Name = name, IsPppoe = pppoe, Connected = connected, Sharing = role });
                }
            }
            catch (Exception cause)
            {
                Dispose();
                throw new IcsFailure(step, cause);
            }
        }

        internal void Enable(Guid id, SharingRole role)
        {
            try { configs[id].EnableSharing(role); }
            catch (Exception cause) { throw new IcsFailure("EnableSharing(" + role + ")", cause); }
        }

        internal void Disable(Guid id)
        {
            try { configs[id].DisableSharing(); }
            catch (Exception cause) { throw new IcsFailure("DisableSharing", cause); }
        }

        internal bool IsSharing(Guid source, Guid target)
        {
            return Connections.Any(c => c.Id == source && c.Connected && c.Sharing == SharingRole.Public) &&
                Connections.Any(c => c.Id == target && c.Connected && c.Sharing == SharingRole.Private);
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
