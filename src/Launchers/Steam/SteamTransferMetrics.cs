using System;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Steam's active-download NETWORK meter. No byte-difference estimate or averaging.
    public sealed class SteamTransferMetrics
    {
        public void Apply(SteamLiveSample sample, UpdateDisplay display)
        {
            display.BytesPerSecond = null;
            if (sample.ReceivedAtUtc == DateTime.MinValue || !sample.HasCounters || display.Paused || display.WaitingForData ||
                sample.DownloadsEnabled == false || !display.Downloaded.HasValue) return;
            display.BytesPerSecond = sample.NetworkBytesPerSecond.HasValue ? (double?)sample.NetworkBytesPerSecond.Value : null;
        }
    }
}
