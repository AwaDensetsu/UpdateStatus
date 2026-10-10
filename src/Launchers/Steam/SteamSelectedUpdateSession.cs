using System;
using System.Diagnostics;
using System.IO;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Created only by an explicit native-library Play with a known pending update.
    // The ordinary adapter remains observation-only.
    public sealed class SteamSelectedUpdateSession : IUpdateCloseSession, IUpdateExitSession
    {
        private readonly uint appId;
        private readonly IUpdateControlSession observer;
        private readonly Func<SteamLiveSample> read;
        private readonly Func<SteamSelectionResult> select;
        private readonly Func<UpdateCommand, bool> send;
        private readonly Func<bool> close;
        private readonly Func<bool?> closeConfirmed;
        private readonly Action prepare, cleanup;
        private readonly Action<string, bool> log;
        private bool prepared, requested, rejected, disposed;
        private readonly Func<DateTime> utcNow;
        private DateTime started, acceptedAt, lastReceipt;
        private DateTime? lastFreshDataAt, cancelledDataMissingSince;
        private int completeSamples;
        private volatile bool manuallyPaused;
        private volatile bool closing;
        private volatile bool selectionCancelled;
        private bool closeInFlight;
        private volatile bool exiting;
        private readonly object selectionCommandGate = new object();
        private readonly object gate = new object();
        public SteamSelectedUpdateSession(uint appId, IUpdateControlSession observer, Func<SteamLiveSample> read,
            Func<SteamSelectionResult> select, Action prepare, Action cleanup, Action<string, bool> log, Func<UpdateCommand, bool> send = null,
            Func<bool> close = null, Func<bool?> closeConfirmed = null, Func<DateTime> utcNow = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            started = this.utcNow();
            this.appId = appId;
            this.observer = observer;
            this.read = read;
            this.select = select;
            this.prepare = prepare;
            this.cleanup = cleanup;
            this.log = log;
            this.send = send ?? observer.SendCommand;
            this.close = close ?? (() => observer is IUpdateCloseSession && ((IUpdateCloseSession)observer).SendClose());
            this.closeConfirmed = closeConfirmed;
        }

        public static IUpdateControlSession Open(LaunchIdentity identity, Action<string, bool> log, Func<SteamCatalogSnapshot> catalog = null)
        {
            uint id = uint.Parse(identity.LauncherId);
            string root = new SteamLocator().FindRoot();
            if (root == null || !SteamUpdateControl.SupportsSelection(root))
                throw new NotSupportedException("Steam update selection is unavailable for this client version");
            var live = new SteamLiveReader(root, id, log);
            var control = new SteamUpdateControl(root, id, log);
            var observer = (IUpdateControlSession)new SteamUpdateAdapter(log).OpenSelected(identity, root, live.TryRead);
            var lifetime = new SteamSelectedClientLifetime();
            return new SteamSelectedUpdateSession(id, observer, () =>
            {
                string currentSession = SteamLocator.SessionId();
                if (!lifetime.Observe(currentSession, false))
                    return null;
                var sample = live.TryRead();
                if (currentSession != SteamLocator.SessionId())
                    return null;
                lifetime.Observe(currentSession, sample != null);
                return WithCatalogGlobals(sample, catalog == null ? null : catalog());
            }, control.Select, () =>
            {
                if (SteamLocator.IsSteamRunning())
                    return;
                // No game URI: Playnite's original launch is retained until completion.
                using (var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "steam.exe"), "-silent")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = root
                }))
                {
                }
                log("Opened Steam for the explicitly selected update; original Play is held", false);
            }, () => { control.Dispose(); live.Dispose(); }, log, control.SendSelected);
        }

        public static SteamLiveSample WithCatalogGlobals(SteamLiveSample sample, SteamCatalogSnapshot catalog)
        {
            if (sample == null || sample.HasCounters || catalog == null || !catalog.Fresh)
                return sample;
            var entry = catalog.Find(sample.AppId);
            if (entry == null || entry.InstallState != sample.InstallState)
                return sample;
            // Download counters can be absent after completion. Fresh catalog getters
            // still report global Pause; no counters or progress are fabricated.
            return new SteamLiveSample
            {
                AppId = sample.AppId,
                InstallState = sample.InstallState,
                DownloadsEnabled = catalog.DownloadsEnabled,
                ReceivedAtUtc = sample.ReceivedAtUtc < catalog.ReceivedUtc ? sample.ReceivedAtUtc : catalog.ReceivedUtc
            };
        }

        public UpdateObservation Poll()
        {
            lock (gate)
            {
                if (disposed)
                    return new UpdateObservation { Phase = UpdatePhase.Idle };
            }
            if (selectionCancelled)
                return ObserveCancelledSelection();
            if (exiting)
                return PollForExit();
            if (rejected)
                return Error("Update interrupted — press Play again");
            SteamLiveSample sample;
            try
            {
                if (!prepared)
                {
                    prepared = true;
                    prepare();
                }
                sample = read();
            }
            catch (Exception e) { rejected = true; log("Selected update interrupted: " + e.Message, false); return Error("Update connection unavailable — press Play again"); }
            var now = utcNow();
            if (sample == null || sample.AppId != appId || now - sample.ReceivedAtUtc > TimeSpan.FromSeconds(5) ||
                sample.ReceivedAtUtc > now || !SteamSelectionPolicy.Known(sample.InstallState) || !sample.DownloadsEnabled.HasValue)
            {
                completeSamples = 0;
                bool expired = lastFreshDataAt.HasValue ? now - lastFreshDataAt.Value >= TimeSpan.FromMinutes(1) :
                    now - started >= TimeSpan.FromMinutes(3);
                if (expired)
                {
                    rejected = true;
                    return Error("Update data unavailable — press Play again");
                }
                if (!lastFreshDataAt.HasValue)
                    return Waiting("Waiting for update data…");
                return new UpdateObservation
                {
                    Phase = UpdatePhase.Updating,
                    Detail = "Waiting for fresh update state",
                    Display = new UpdateDisplay
                    {
                        Stage = "Update data unavailable",
                        WaitingForData = true,
                        Notice = "Update data unavailable — retrying…"
                    }
                };
            }
            lastFreshDataAt = sample.ReceivedAtUtc;
            if ((sample.InstallState & 8192) != 0)
                return new UpdateObservation { Phase = UpdatePhase.Running };
            if (rejected)
                return Error("Update selection not accepted — check the launcher and press Play again");
            if (!requested && SteamSelectionPolicy.Pending(sample.InstallState))
            {
                lock (gate)
                {
                    if (disposed)
                        return new UpdateObservation { Phase = UpdatePhase.Idle };
                }
                SteamSelectionResult result;
                lock (selectionCommandGate)
                {
                    lock (gate)
                    {
                        if (disposed)
                            return new UpdateObservation { Phase = UpdatePhase.Idle };
                    }
                    if (selectionCancelled || exiting)
                        return Waiting("Closing selected update…");
                    result = select();
                }
                lock (gate)
                {
                    if (disposed)
                        return new UpdateObservation { Phase = UpdatePhase.Idle };
                }
                if (result == SteamSelectionResult.Waiting)
                    return Waiting("Waiting for stable update state…");
                if (result == SteamSelectionResult.Rejected)
                {
                    rejected = true;
                    return Error("Update selection not accepted — check the launcher and press Play again");
                }
                requested = true;
                acceptedAt = utcNow();
                log("Selected update AppID=" + appId + "; priority accepted, waiting for live completion", false);
                // This frame predates the command. Never release Play from it.
                return Waiting("Starting selected update…");
            }
            if (SteamSelectionPolicy.Complete(sample.InstallState) && sample.UpdateState == 0)
            {
                // Buffered replies received during the selection command belong
                // to the earlier state and cannot release this retained startup.
                if (requested && sample.ReceivedAtUtc <= acceptedAt)
                {
                    completeSamples = 0;
                    return Waiting("Waiting for fresh selected-update completion…");
                }
                if (sample.ReceivedAtUtc > lastReceipt)
                {
                    lastReceipt = sample.ReceivedAtUtc;
                    completeSamples++;
                }
                if (completeSamples >= 2 && sample.DownloadsEnabled == true && !manuallyPaused)
                    return new UpdateObservation { Phase = UpdatePhase.Ready, Percent = 100, ControlPaused = false, Detail = "Selected Steam update complete" };
                // Keep global controls available if completion coincides with Pause.
                return new UpdateObservation
                {
                    Phase = UpdatePhase.Updating,
                    ControlPaused = sample.DownloadsEnabled.HasValue ? !sample.DownloadsEnabled : null,
                    ControlScope = "Steam downloads",
                    CanResume = sample.DownloadsEnabled == false,
                    CanPause = sample.DownloadsEnabled == true,
                    Display = new UpdateDisplay { Stage = sample.DownloadsEnabled == false ? "Paused" : manuallyPaused && completeSamples >= 2 ? "Update complete" : "Finalizing", Paused = sample.DownloadsEnabled == false }
                };
            }
            completeSamples = 0;
            var observed = observer.Poll();
            // Saved error 6 used to abort Play before Steam could retry. Issue the
            // explicit request first and allow fresh native state to replace it.
            if (observed.Phase == UpdatePhase.Error && requested && utcNow() - acceptedAt < TimeSpan.FromSeconds(15))
                return Waiting("Starting selected update…");
            // Completion authority comes only from fresh update flags above.
            if (observed.Phase == UpdatePhase.Ready || observed.Phase == UpdatePhase.Launching || observed.Phase == UpdatePhase.Idle)
                return Waiting("Waiting for selected update…");
            if (observed.Phase == UpdatePhase.Updating && sample.DownloadsEnabled.HasValue)
            {
                observed.ControlPaused = !sample.DownloadsEnabled.Value;
                observed.ControlScope = "Steam downloads";
                observed.CanPause = sample.DownloadsEnabled.Value;
                observed.CanResume = !sample.DownloadsEnabled.Value;
            }
            return observed;
        }
        private static UpdateObservation Waiting(string detail)
        {
            return new UpdateObservation { Phase = UpdatePhase.Updating, Display = new UpdateDisplay { Stage = "Starting update", WaitingForData = true }, Detail = detail };
        }
        private static UpdateObservation Error(string detail)
        {
            return new UpdateObservation { Phase = UpdatePhase.Error, Detail = detail, UserMessage = detail, CancelPendingPlay = true };
        }
        public void PrepareForExit()
        {
            lock (gate)
                exiting = true;
        }
        private UpdateObservation PollForExit()
        {
            var sample = read();
            if (sample == null || sample.AppId != appId || sample.ReceivedAtUtc > utcNow() ||
                utcNow() - sample.ReceivedAtUtc > TimeSpan.FromSeconds(5) || !SteamSelectionPolicy.Known(sample.InstallState))
                return Waiting("Waiting for exit confirmation…");
            if ((sample.InstallState & 8192) != 0)
                return new UpdateObservation { Phase = UpdatePhase.Running };
            if (SteamSelectionPolicy.Complete(sample.InstallState) && sample.UpdateState == 0)
                return new UpdateObservation { Phase = UpdatePhase.Ready };
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating,
                ControlPaused = sample.DownloadsEnabled.HasValue ? !sample.DownloadsEnabled : null,
                ControlScope = "Steam downloads"
            };
        }
        public bool SendCommand(UpdateCommand command)
        {
            bool previous;
            lock (gate)
            {
                if (disposed || rejected)
                    return false;
                previous = manuallyPaused;
                if (command == UpdateCommand.Pause)
                    manuallyPaused = true;
            }
            bool accepted;
            try
            {
                accepted = send(command);
            }
            catch { lock (gate) manuallyPaused = previous; throw; }
            lock (gate)
            {
                if (!accepted)
                    manuallyPaused = previous;
                else if (command == UpdateCommand.Resume)
                    manuallyPaused = false;
            }
            return accepted;
        }
        public bool SendClose()
        {
            lock (gate)
            {
                if (disposed || closeInFlight)
                    return false;
                // Cancelling Play is permanent for this selection. Command failure
                // must not revive initial queue placement or completion authority.
                selectionCancelled = true;
                closeInFlight = true;
            }
            bool accepted = false;
            try
            {
                // Let an existing initial selection finish before queue removal.
                lock (selectionCommandGate)
                {
                    lock (gate)
                    {
                        if (disposed)
                            return false;
                    }
                    accepted = close();
                }
                lock (gate)
                    return !disposed && accepted;
            }
            finally
            {
                lock (gate)
                {
                    closing = accepted;
                    closeInFlight = false;
                }
            }
        }
        private UpdateObservation ObserveCancelledSelection()
        {
            UpdateObservation observed;
            try
            {
                observed = observer.Poll() ?? new UpdateObservation { Phase = UpdatePhase.Updating };
                if (closing && closeConfirmed != null)
                    observed.CloseConfirmed = closeConfirmed();
            }
            catch (Exception error)
            {
                if (!cancelledDataMissingSince.HasValue)
                    log("Cancelled update observation unavailable: " + error.GetType().Name, false);
                observed = null;
            }
            if (observed == null)
                observed = new UpdateObservation { Phase = UpdatePhase.Updating };
            if (observed.Phase == UpdatePhase.Running)
                return observed;
            var now = utcNow();
            bool fresh = observed.ReceivedAtUtc.HasValue && observed.ReceivedAtUtc.Value <= now &&
                now - observed.ReceivedAtUtc.Value <= TimeSpan.FromSeconds(5) &&
                (observed.Phase == UpdatePhase.Updating || observed.Phase == UpdatePhase.Ready);
            // Removing the item can make progress unavailable. Fresh queue
            // confirmation independently completes Close without releasing Play.
            if (!fresh && observed.CloseConfirmed != true)
            {
                if (!cancelledDataMissingSince.HasValue)
                    cancelledDataMissingSince = now;
                if (now - cancelledDataMissingSince.Value >= TimeSpan.FromMinutes(1))
                    return Error("Update data unavailable — check the launcher or press Play again");
                // An Error reply is not fresh update state. Preserve its detail
                // during recovery, but do not let repeated errors bypass the deadline.
                if (observed.Phase == UpdatePhase.Error)
                    return observed;
                return new UpdateObservation
                {
                    Phase = UpdatePhase.Updating,
                    Display = new UpdateDisplay
                    {
                        Stage = "Update data unavailable",
                        WaitingForData = true,
                        Notice = "Update data unavailable — retrying…"
                    }
                };
            }
            cancelledDataMissingSince = null;
            if (observed.Phase != UpdatePhase.Ready && observed.Phase != UpdatePhase.Launching && observed.Phase != UpdatePhase.Idle)
                return observed;
            bool paused = observed.ControlPaused == true || observed.Display != null && observed.Display.Paused;
            bool complete = observed.Phase == UpdatePhase.Ready;
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating,
                ControlPaused = observed.ControlPaused,
                ControlScope = observed.ControlScope,
                CanPause = observed.CanPause,
                CanResume = observed.CanResume,
                CloseConfirmed = observed.CloseConfirmed,
                Detail = observed.Detail,
                Display = new UpdateDisplay
                {
                    Stage = paused ? "Paused" : complete ? "Update complete" : "Update status unavailable",
                    Paused = paused,
                    WaitingForData = !complete && !paused
                }
            };
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
            }
            cleanup();
            observer.Dispose();
        }
    }
}
