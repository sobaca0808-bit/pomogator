using System;

namespace PomogatorWifi
{
    public sealed class HostedTarget
    {
        public Guid Adapter { get; set; }
        public string Bssid { get; set; }
        public string SsidBytes { get; set; }

        internal bool Matches(HostedTarget other) => other != null && Adapter != Guid.Empty &&
            Adapter == other.Adapter && Bssid == other.Bssid && SsidBytes == other.SsidBytes;
    }

    internal static class HostedStop
    {
        internal static void Apply(HostedTarget approved, long observedGeneration, Func<HostedTarget> read,
            Func<long> generation, Action stop)
        {
            RequireMatch(approved, observedGeneration, read(), generation());
            stop();
        }

        internal static bool WaitForOff(Func<HostedNetwork.State> read, Action pause)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (read() != HostedNetwork.State.Active) return true;
                if (attempt < 11) pause();
            }
            return false;
        }

        internal static void RequireMatch(HostedTarget approved, long expectedGeneration, HostedTarget current, long generation)
        {
            if (approved == null || !approved.Matches(current) || generation != expectedGeneration)
                throw new WifiError("Проверенная точка изменилась. Обновите состояние и подтвердите отключение заново.");
        }
    }
}
