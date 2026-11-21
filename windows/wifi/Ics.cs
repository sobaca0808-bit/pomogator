using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PomogatorWifi
{
    internal sealed class IcsSnapshot : IDisposable
    {
        private readonly List<object> comObjects = new List<object>();
        private readonly Dictionary<Guid, object> configs = new Dictionary<Guid, object>();
        internal readonly List<Connection> Connections = new List<Connection>();

        internal IcsSnapshot()
        {
            try
            {
                var manager = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.HNetShare", true)));
                if (!(bool)Get(manager, "SharingInstalled"))
                    throw new WifiError("Общий доступ к интернету (ICS) недоступен. Проверьте службу «Общий доступ к подключению к Интернету (ICS)» в services.msc.");
                var collection = (IEnumerable)Keep(Get(manager, "EnumEveryConnection"));
                foreach (var item in collection)
                {
                    Keep(item);
                    var props = Keep(Get(manager, "NetConnectionProps", item));
                    Guid id;
                    if (!Guid.TryParse((string)Get(props, "Guid"), out id) || id == Guid.Empty || configs.ContainsKey(id))
                        throw new WifiError("Windows вернула неоднозначный список подключений. Проверьте адаптеры в ncpa.cpl.");
                    var config = Keep(Get(manager, "INetSharingConfigurationForINetConnection", item));
                    configs.Add(id, config);
                    Connections.Add(new Connection {
                        Id = id,
                        Name = (string)Get(props, "Name"),
                        IsPppoe = Convert.ToInt32(Get(props, "MediaType")) == 6, // NCM_PPPOE
                        Connected = Convert.ToInt32(Get(props, "Status")) == 2, // NCS_CONNECTED
                        Sharing = (bool)Get(config, "SharingEnabled")
                            ? (SharingRole?)Convert.ToInt32(Get(config, "SharingConnectionType")) : null
                    });
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Enable(Guid id, SharingRole role)
        {
            Call(configs[id], "EnableSharing", (int)role);
        }

        internal void Disable(Guid id)
        {
            Call(configs[id], "DisableSharing");
        }

        internal bool IsSharing(Guid source, Guid target)
        {
            return Connections.Any(c => c.Id == source && c.Connected && c.Sharing == SharingRole.Public) &&
                Connections.Any(c => c.Id == target && c.Connected && c.Sharing == SharingRole.Private);
        }

        private object Keep(object value)
        {
            if (value != null && Marshal.IsComObject(value)) comObjects.Add(value);
            return value;
        }

        private static object Get(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);
        }

        private static void Call(object target, string name, params object[] args)
        {
            target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }

        public void Dispose()
        {
            for (int index = comObjects.Count - 1; index >= 0; index--)
                Marshal.ReleaseComObject(comObjects[index]);
            comObjects.Clear();
        }
    }
}
