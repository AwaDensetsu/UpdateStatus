using System;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamStageProgress
    {
        public ulong Done, Total;
        public float Weight;
    }

    public static class SteamLivePresentation
    {
        // Stage and weighted work presentation follow the reviewed Steam contract.
        // SteamAbiResolver validates that contract before native data is read.
        public static UpdateDisplay Build(SteamLiveSample sample)
        {
            bool knownState = (sample.UpdateState & ~2047u) == 0;
            bool paused = sample.Result == 2 || sample.Result == 4 || (sample.InstallState & 512) != 0;
            return new UpdateDisplay {
                Downloaded = sample.HasCounters && knownState ? (ulong?)sample.Downloaded : null,
                DownloadTotal = sample.HasCounters && knownState ? (ulong?)sample.DownloadTotal : null,
                DownloadPercent = sample.HasCounters && knownState ? Ratio(sample.Downloaded, sample.DownloadTotal) : null,
                Stage = paused ? "Paused" : Stage(sample.UpdateState, sample.InstallState),
                Paused = paused,
                WaitingForData = !sample.HasCounters || !knownState,
                SecondsRemaining = sample.HasCounters && knownState && !paused && (sample.UpdateState & 1) != 0 && sample.SecondsRemaining > 0 ? (int?)sample.SecondsRemaining : null,
                WorkPercent = sample.HasCounters && knownState && ((sample.UpdateState & 1) != 0 || paused) ? WorkPercent(sample.Progress) : null
            };
        }
        public static string Stage(uint state, uint installState)
        {
            if ((state & ~2047u) != 0) return "Updating files";
            if ((installState & 512) != 0) return "Paused";
            if ((state & 1) == 0) return (installState & 2) != 0 ? "Queued" : "Updating files";
            if ((state & 1024) != 0) return "Stopping";
            if ((state & 256) != 0) return "Installing files";
            if ((state & 128) != 0) return "Verifying files";
            if ((state & 2) != 0) return "Starting download";
            if ((state & 4) != 0) return "Validating files";
            if ((state & 16) != 0) return "Reserving space";
            if ((state & 512) != 0) return "Finalizing";
            bool installed = (installState & SteamManifest.FullyInstalled) != 0;
            if ((state & 32) != 0)
                return (state & 64) != 0 ? (installed ? "Updating files" : "Installing files") : "Preloading";
            if ((state & 64) != 0) return installed ? "Patching files" : "Unpacking files";
            return "Updating files";
        }
        public static int? Ratio(ulong done, ulong total)
        {
            if (total == 0 || done > total) return null;
            return Math.Max(done > 0 ? 1 : 0, Math.Min(100, (int)decimal.Floor((decimal)done * 100 / total)));
        }
        public static int? WorkPercent(SteamStageProgress[] stages)
        {
            if (stages == null || stages.Length != 7) return null;
            // Steam runs download/staging concurrently. It excludes the one with
            // the smaller byte total (download on ties) from BOTH weighted sums.
            int excluded = stages[2].Total <= stages[3].Total ? 2 : 3;
            decimal done = 0, total = 0;
            try
            {
                for (int i = 0; i < 7; i++)
                {
                    var s = stages[i];
                    if (s == null || s.Done > s.Total || float.IsNaN(s.Weight) || float.IsInfinity(s.Weight) || s.Weight < 0) return null;
                    if (i == excluded) continue;
                    // Match Steam's single-precision product then unsigned integer
                    // truncation per stage, rather than inventing equal weights.
                    float d = (float)s.Done * s.Weight, t = (float)s.Total * s.Weight;
                    if (float.IsInfinity(d) || float.IsInfinity(t) || t >= 18446744073709551616f) return null;
                    done += (ulong)d; total += (ulong)t;
                }
                if (total == 0 || done > total) return null;
                return Math.Max(done > 0 ? 1 : 0, Math.Min(100, (int)decimal.Floor(done * 100 / total)));
            }
            catch (OverflowException) { return null; }
        }
    }
}
