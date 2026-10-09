using System;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamProgress
    {
        private int completeSamples;
        private bool seenUpdate, readyEmitted;

        public UpdateObservation Evaluate(SteamManifest m, bool logActive, bool freshCounters, string logError)
        {
            var result = new UpdateObservation { Phase = UpdatePhase.Launching };
            if (m.IsRunning) { result.Phase = UpdatePhase.Running; return result; }
            bool updating = m.Updating || logActive;
            if (updating)
            {
                completeSamples = 0; readyEmitted = false; seenUpdate = true;
                result.Phase = UpdatePhase.Updating;
                if (logError != null) { result.Phase = UpdatePhase.Error; result.Detail = logError; return result; }
                result.Display = new UpdateDisplay { Stage = ManifestStage(m), Paused = (m.Flags.GetValueOrDefault() & 512) != 0 || m.UpdateResult == 2 || m.UpdateResult == 4 };
                if (result.Display.Paused) result.Display.Stage = "Paused";
                // Queued/required-only flags and old counters cannot measure a new update.
                if ((m.Active || (logActive && freshCounters)) && freshCounters && m.KnownFlags)
                {
                    if (m.Downloaded.HasValue && m.DownloadTotal.HasValue && m.Downloaded <= m.DownloadTotal)
                    {
                        result.Display.Downloaded = m.Downloaded;
                        result.Display.DownloadTotal = m.DownloadTotal;
                        result.Display.DownloadPercent = SteamLivePresentation.Ratio(m.Downloaded.Value, m.DownloadTotal.Value);
                    }
                    // Manifests do not contain the seven Steam work weights. Never
                    // substitute download or staging ratios for Steam's disk percentage.
                }
            }
            else if (seenUpdate && m.Installed && m.KnownFlags &&
                (!m.TargetBuildId.HasValue || !m.BuildId.HasValue || m.TargetBuildId == m.BuildId))
            {
                if (!readyEmitted)
                {
                    if (++completeSamples >= 2) { result.Phase = UpdatePhase.Ready; result.Percent = 100; readyEmitted = true; }
                    else { result.Phase = UpdatePhase.Updating; result.CompletionPending = true; result.Display = new UpdateDisplay { Stage = "Finalizing" }; }
                }
            }
            else completeSamples = 0;
            return result;
        }
        private static string ManifestStage(SteamManifest m)
        {
            ulong flags = m.Flags ?? 0;
            if (!m.KnownFlags) return "Updating files";
            if ((flags & 8388608) != 0) return "Stopping";
            if ((flags & 512) != 0) return "Paused";
            if ((flags & 4194304) != 0) return "Finalizing";
            if ((flags & 131072) != 0) return "Verifying files";
            if ((flags & 524288) != 0) return "Reserving space";
            if ((flags & 65536) != 0) return "Starting download";
            if ((flags & (262144 | 2097152)) != 0) return (flags & SteamManifest.FullyInstalled) != 0 ? "Updating files" : "Installing files";
            if ((flags & 1048576) != 0) return "Downloading";
            return (flags & 2) != 0 && !m.Active ? "Queued" : "Updating files";
        }
    }
}
