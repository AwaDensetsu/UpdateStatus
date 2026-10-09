using System;
using System.Linq;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public static class SteamQueueClosePolicy
    {
        public static bool? Confirmed(SteamQueueSample sample, uint appId, DateTime notBefore = default(DateTime))
        {
            if (sample == null || sample.ReceivedUtc <= notBefore || DateTime.UtcNow-sample.ReceivedUtc > TimeSpan.FromSeconds(5) || sample.ReceivedUtc > DateTime.UtcNow ||
                !sample.DownloadsEnabled.HasValue || !sample.ActiveAppId.HasValue || !sample.QueueCount.HasValue) return null;
            var row = sample.Items.SingleOrDefault(item => item.AppId == appId);
            if (row == null || !row.Flags.HasValue || !row.QueueIndex.HasValue || !SteamSelectionPolicy.Known(row.Flags.Value)) return null;
            return sample.DownloadsEnabled == false && sample.ActiveAppId != appId && row.QueueIndex == -1 && (row.Flags.Value & (8 | 256 | 8192)) == 0;
        }
    }
}
