using System;
using System.Linq;
using System.Reflection;

namespace PomogatorWifi
{
    internal enum WifiPhase { Off, Starting, Owned, External, Stopping, Unavailable, Error, Unconfirmed }
    internal enum MobilePhase { Unknown, Off, On, Transition }

    internal static class WifiRules
    {
        internal static WifiPhase Phase(bool known, HostedNetwork.State hosted, bool owns,
            MobilePhase mobile, bool sharedWifiDirect, bool ownerKnown = true)
        {
            if (!ownerKnown) return WifiPhase.Unconfirmed;
            if (hosted == HostedNetwork.State.Active) return owns ? WifiPhase.Owned : WifiPhase.External;
            if (mobile == MobilePhase.On || mobile == MobilePhase.Transition || sharedWifiDirect) return WifiPhase.External;
            return !known || hosted == HostedNetwork.State.Unavailable ? WifiPhase.Unavailable : WifiPhase.Off;
        }

        internal static bool SamePowerIntent(WifiPhase before, Guid owner, Guid adapter,
            WifiPhase current, Guid currentOwner, Guid currentAdapter)
        {
            if (before != current || owner != currentOwner) return false;
            return before == WifiPhase.Off || before == WifiPhase.Owned && adapter != Guid.Empty && adapter == currentAdapter;
        }

        internal static bool AutoStart(bool enabled, WifiPhase wifi, bool hasPassword) =>
            enabled && wifi == WifiPhase.Off && hasPassword;

        internal static bool AutomaticTarget(Guid approved, Guid observed) => approved != Guid.Empty && approved == observed;
    }

    internal sealed class RecoveryEdge
    {
        private bool wasOffline;
        private Guid resumedOwner, observedOwner;
        private DateTime resumeDeadline;

        internal void Resumed(Guid owner)
        {
            resumedOwner = owner;
            resumeDeadline = DateTime.UtcNow.AddSeconds(45);
        }

        internal bool Observe(bool enabled, Guid owner, bool? sourceConnected, bool needsRecovery)
        {
            if (observedOwner != owner) wasOffline = false;
            observedOwner = owner;
            var restored = Observe(enabled, owner != Guid.Empty, sourceConnected);
            var shouldResume = enabled && owner != Guid.Empty && owner == resumedOwner &&
                DateTime.UtcNow < resumeDeadline && sourceConnected == true && needsRecovery;
            if (!enabled || restored || shouldResume) resumedOwner = Guid.Empty;
            return restored || shouldResume;
        }

        internal bool Observe(bool enabled, bool owned, bool? sourceConnected)
        {
            if (!enabled || !owned) { wasOffline = false; return false; }
            if (sourceConnected == false) { wasOffline = true; return false; }
            if (sourceConnected != true || !wasOffline) return false;
            wasOffline = false;
            return true;
        }
    }

    internal static class MobileHotspot
    {
        internal static MobilePhase Read()
        {
            try
            {
                var information = FindType("Connectivity.NetworkInformation");
                var manager = FindType("NetworkOperators.NetworkOperatorTetheringManager");
                var profile = information?.GetMethod("GetInternetConnectionProfile", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, null);
                if (manager == null || profile == null) return MobilePhase.Unknown;
                var factory = manager.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(method =>
                    method.Name == "CreateFromConnectionProfile" && method.GetParameters().Length == 1);
                var current = factory?.Invoke(null, new[] { profile });
                var state = manager.GetProperty("TetheringOperationalState")?.GetValue(current)?.ToString();
                return state == "On" ? MobilePhase.On : state == "Off" ? MobilePhase.Off :
                    state == "InTransition" ? MobilePhase.Transition : MobilePhase.Unknown;
            }
            catch { return MobilePhase.Unknown; }
        }

        private static Type FindType(string name) =>
            Type.GetType("Windows.Networking." + name + ", Windows, ContentType=WindowsRuntime", false);
    }
}
