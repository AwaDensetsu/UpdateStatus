using System;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public static class SteamLivePresentation
    {
        // Work percentage comes directly from Steam UI; downloaded bytes have their own percentage.
        public static UpdateDisplay Build(SteamLiveSample sample, string previousStage = null)
        {
            bool knownState = (sample.UpdateState & ~2047u) == 0;
            bool paused = sample.Result == 2 || sample.Result == 4 || (sample.InstallState & 512) != 0 ||
                sample.DownloadsEnabled == false && (sample.InstallState & SteamManifest.UpdateMask) != 0;
            string stage = paused ? "Paused" : Stage(sample.UpdateState, sample.InstallState);
            if (!sample.HasCounters && !paused && previousStage != null && previousStage != "Paused") stage = previousStage;
            return new UpdateDisplay {
                Downloaded = sample.HasCounters && knownState ? (ulong?)sample.Downloaded : null,
                DownloadTotal = sample.HasCounters && knownState ? (ulong?)sample.DownloadTotal : null,
                DownloadPercent = sample.HasCounters && knownState ? Ratio(sample.Downloaded, sample.DownloadTotal) : null,
                Stage = stage,
                Paused = paused,
                WaitingForData = !sample.HasCounters || !knownState,
                SecondsRemaining = sample.HasCounters && knownState && !paused && (sample.UpdateState & 1) != 0 && sample.SecondsRemaining > 0 ? (int?)sample.SecondsRemaining : null,
                WorkPercent = sample.HasCounters && knownState && ((sample.UpdateState & 1) != 0 || paused) ? sample.UiWorkPercent : null
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
    }
}
