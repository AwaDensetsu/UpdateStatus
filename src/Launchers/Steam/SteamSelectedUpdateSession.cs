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
        private readonly Func<UpdateCommand,bool> send;
        private readonly Func<bool> close;
        private readonly Func<bool?> closeConfirmed;
        private readonly Action prepare, cleanup;
        private readonly Action<string,bool> log;
        private bool prepared, requested, rejected, disposed;
        private DateTime started = DateTime.UtcNow, acceptedAt, lastReceipt;
        private int completeSamples;
        private volatile bool manuallyPaused;
        private volatile bool closing;
        private volatile bool exiting;
        private readonly object selectionCommandGate = new object();
        private readonly object gate = new object();
        public SteamSelectedUpdateSession(uint appId, IUpdateControlSession observer, Func<SteamLiveSample> read,
            Func<SteamSelectionResult> select, Action prepare, Action cleanup, Action<string,bool> log, Func<UpdateCommand,bool> send = null,
            Func<bool> close = null, Func<bool?> closeConfirmed = null)
        {
            this.appId = appId; this.observer = observer; this.read = read; this.select = select; this.prepare = prepare; this.cleanup = cleanup; this.log = log; this.send = send ?? observer.SendCommand;
            this.close = close ?? (() => observer is IUpdateCloseSession && ((IUpdateCloseSession)observer).SendClose()); this.closeConfirmed = closeConfirmed;
        }

        public static IUpdateControlSession Open(LaunchIdentity identity, Action<string,bool> log, Func<SteamCatalogSnapshot> catalog = null)
        {
            uint id = uint.Parse(identity.LauncherId); string root = new SteamLocator().FindRoot();
            if (root == null || !SteamUpdateControl.SupportsSelection(root)) throw new NotSupportedException("Steam update selection is unavailable for this client version");
            var live = new SteamLiveReader(root,id,log);
            var control = new SteamUpdateControl(root,id,log);
            var observer = (IUpdateControlSession)new SteamUpdateAdapter(log).Open(identity);
            var lifetime = new SteamSelectedClientLifetime();
            return new SteamSelectedUpdateSession(id,observer,() => {
                string currentSession = SteamLocator.SessionId();
                if (!lifetime.Observe(currentSession,false)) return null;
                var sample = live.TryRead();
                if (currentSession != SteamLocator.SessionId()) return null;
                lifetime.Observe(currentSession,sample != null);
                return WithCatalogGlobals(sample,catalog == null ? null : catalog());
            },control.Select,() => {
                if (SteamLocator.IsSteamRunning()) return;
                // No game URI: Playnite's original launch is retained until completion.
                using (var process = Process.Start(new ProcessStartInfo(Path.Combine(root,"steam.exe"),"-silent")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root })) { }
                log("Opened Steam for the explicitly selected update; original Play is held",false);
            },() => { control.Dispose(); live.Dispose(); },log,control.SendSelected);
        }

        public static SteamLiveSample WithCatalogGlobals(SteamLiveSample sample, SteamCatalogSnapshot catalog)
        {
            if (sample == null || sample.HasCounters || catalog == null || !catalog.Fresh) return sample;
            var entry = catalog.Find(sample.AppId);
            if (entry == null || entry.InstallState != sample.InstallState) return sample;
            // GetUpdateInfo can be absent after completion. Fresh catalog getters
            // still report global Pause; no counters or progress are fabricated.
            return new SteamLiveSample { AppId = sample.AppId, InstallState = sample.InstallState, DownloadsEnabled = catalog.DownloadsEnabled,
                ReceivedAtUtc = sample.ReceivedAtUtc < catalog.ReceivedUtc ? sample.ReceivedAtUtc : catalog.ReceivedUtc };
        }

        public UpdateObservation Poll()
        {
            lock (gate) { if (disposed) return new UpdateObservation { Phase = UpdatePhase.Idle }; }
            if (closing)
            {
                var closed = observer.Poll();
                if (closeConfirmed != null) closed.CloseConfirmed = closeConfirmed();
                if (closed.Phase == UpdatePhase.Ready || closed.Phase == UpdatePhase.Launching || closed.Phase == UpdatePhase.Idle)
                { closed.Phase = UpdatePhase.Updating; closed.Display = new UpdateDisplay { Stage = "Paused", Paused = true }; }
                return closed;
            }
            if (exiting) return PollForExit();
            if (rejected) return Error("Selected Play interrupted — check the launcher and press Play again");
            SteamLiveSample sample;
            try { if (!prepared) { prepared = true; prepare(); } sample = read(); }
            catch (Exception e) { rejected = true; log("Selected update interrupted: " + e.Message,false); return Error(e.Message); }
            if (sample == null || sample.AppId != appId || (DateTime.UtcNow-sample.ReceivedAtUtc).TotalSeconds > 5 ||
                sample.ReceivedAtUtc > DateTime.UtcNow || !SteamSelectionPolicy.Known(sample.InstallState))
            {
                completeSamples = 0;
                return DateTime.UtcNow-started > TimeSpan.FromMinutes(3) && !requested
                    ? Error("Update state unavailable — check the launcher") : Waiting("Waiting for update data…");
            }
            if ((sample.InstallState & 8192) != 0) return new UpdateObservation { Phase = UpdatePhase.Running };
            if (rejected) return Error("Update selection not accepted — check the launcher and press Play again");
            if (!requested && SteamSelectionPolicy.Pending(sample.InstallState))
            {
                lock (gate) { if (disposed) return new UpdateObservation { Phase = UpdatePhase.Idle }; }
                SteamSelectionResult result;
                lock (selectionCommandGate)
                {
                    lock (gate) { if (disposed) return new UpdateObservation { Phase = UpdatePhase.Idle }; }
                    if (closing || exiting) return Waiting("Closing selected update…");
                    result = select();
                }
                lock (gate) { if (disposed) return new UpdateObservation { Phase = UpdatePhase.Idle }; }
                if (result == SteamSelectionResult.Waiting) return Waiting("Waiting for stable update state…");
                if (result == SteamSelectionResult.Rejected) { rejected = true; return Error("Update selection not accepted — check the launcher and press Play again"); }
                requested = true; acceptedAt = DateTime.UtcNow;
                log("Selected update AppID=" + appId + "; priority accepted, waiting for live completion",false);
                // This frame predates the command. Never release Play from it.
                return Waiting("Starting selected update…");
            }
            if (SteamSelectionPolicy.Complete(sample.InstallState) && sample.UpdateState == 0)
            {
                // Buffered replies received during the selection command belong
                // to the earlier state and cannot release this retained startup.
                if (requested && sample.ReceivedAtUtc <= acceptedAt) { completeSamples = 0; return Waiting("Waiting for fresh selected-update completion…"); }
                if (sample.ReceivedAtUtc > lastReceipt) { lastReceipt = sample.ReceivedAtUtc; completeSamples++; }
                if (completeSamples >= 2 && sample.DownloadsEnabled == true && !manuallyPaused)
                    return new UpdateObservation { Phase = UpdatePhase.Ready, Percent = 100, ControlPaused = false, Detail = "Selected Steam update complete" };
                // Keep global controls available if completion coincides with Pause.
                return new UpdateObservation { Phase = UpdatePhase.Updating, ControlPaused = sample.DownloadsEnabled.HasValue ? !sample.DownloadsEnabled : null,
                    ControlScope = "Steam downloads", CanResume = sample.DownloadsEnabled == false, CanPause = sample.DownloadsEnabled == true,
                    Display = new UpdateDisplay { Stage = sample.DownloadsEnabled == false || manuallyPaused ? "Paused" : "Finalizing", Paused = sample.DownloadsEnabled == false || manuallyPaused } };
            }
            completeSamples = 0;
            var observed = observer.Poll();
            // Saved error 6 used to abort Play before Steam could retry. Issue the
            // explicit request first and allow fresh native state to replace it.
            if (observed.Phase == UpdatePhase.Error && requested && DateTime.UtcNow-acceptedAt < TimeSpan.FromSeconds(15))
                return Waiting("Starting selected update…");
            // Completion authority comes only from fresh native flags above.
            if (observed.Phase == UpdatePhase.Ready || observed.Phase == UpdatePhase.Launching || observed.Phase == UpdatePhase.Idle)
                return Waiting("Waiting for selected update…");
            if (observed.Phase == UpdatePhase.Updating && sample.DownloadsEnabled.HasValue)
            {
                observed.ControlPaused = !sample.DownloadsEnabled.Value; observed.ControlScope = "Steam downloads";
                observed.CanPause = sample.DownloadsEnabled.Value; observed.CanResume = !sample.DownloadsEnabled.Value;
            }
            return observed;
        }
        private static UpdateObservation Waiting(string detail)
        { return new UpdateObservation { Phase = UpdatePhase.Updating, Display = new UpdateDisplay { Stage = "Starting update", WaitingForData = true }, Detail = detail }; }
        private static UpdateObservation Error(string detail) { return new UpdateObservation { Phase = UpdatePhase.Error, Detail = detail }; }
        public void PrepareForExit() { lock (gate) exiting = true; }
        private UpdateObservation PollForExit()
        {
            var sample = read();
            if (sample == null || sample.AppId != appId || sample.ReceivedAtUtc > DateTime.UtcNow ||
                DateTime.UtcNow-sample.ReceivedAtUtc > TimeSpan.FromSeconds(5) || !SteamSelectionPolicy.Known(sample.InstallState))
                return Waiting("Waiting for exit confirmation…");
            if ((sample.InstallState & 8192) != 0) return new UpdateObservation { Phase = UpdatePhase.Running };
            if (SteamSelectionPolicy.Complete(sample.InstallState) && sample.UpdateState == 0) return new UpdateObservation { Phase = UpdatePhase.Ready };
            return new UpdateObservation { Phase = UpdatePhase.Updating, ControlPaused = sample.DownloadsEnabled.HasValue ? !sample.DownloadsEnabled : null,
                ControlScope = "Steam downloads" };
        }
        public bool SendCommand(UpdateCommand command)
        {
            bool previous;
            lock (gate) { if (disposed || rejected) return false; previous = manuallyPaused; if (command == UpdateCommand.Pause) manuallyPaused = true; }
            bool accepted;
            try { accepted = send(command); }
            catch { lock (gate) manuallyPaused = previous; throw; }
            lock (gate) { if (!accepted) manuallyPaused = previous; else if (command == UpdateCommand.Resume) manuallyPaused = false; }
            return accepted;
        }
        public bool SendClose()
        {
            lock (gate) { if (disposed) return false; closing = true; manuallyPaused = true; }
            // An in-flight initial selection must finish before removal. It cannot
            // requeue/enable the app after the close command has succeeded.
            lock (selectionCommandGate)
            {
                lock (gate) { if (disposed) return false; }
                return close();
            }
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; } cleanup(); observer.Dispose(); }
    }
}
