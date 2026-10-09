using System;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // A persisted checkpoint is not live telemetry. Expiry only removes a number;
    // elapsed time never generates or advances progress.
    public sealed class SteamCounterFreshness
    {
        private string previous;
        private DateTime? changedAt;
        public bool Observe(SteamManifest manifest, DateTime now)
        {
            string signature = manifest.Downloaded + "/" + manifest.DownloadTotal + ";" +
                manifest.Staged + "/" + manifest.StageTotal + ";" + manifest.TargetBuildId;
            if (previous != null && signature != previous) changedAt = now;
            previous = signature;
            return changedAt.HasValue && now >= changedAt.Value && (now - changedAt.Value).TotalSeconds <= 5;
        }
        public void Reset() { previous = null; changedAt = null; }
    }
}
