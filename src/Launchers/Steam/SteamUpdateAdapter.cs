using System;
using System.IO;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamUpdateAdapter : ILauncherUpdateAdapter
    {
        private readonly Action<string, bool> log;
        public string Name { get { return "Steam"; } }
        public SteamUpdateAdapter(Action<string, bool> log) { this.log = log; }
        public IUpdateSession Open(LaunchIdentity game) { return new Session(game, log); }

        // The selected session owns this source and disposes it after observation.
        // Reusing its samples avoids a second helper for the same game.
        internal IUpdateSession OpenSelected(LaunchIdentity game, string steamRoot, Func<SteamLiveSample> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            return new Session(game, log, steamRoot, read);
        }

        private sealed class Session : IUpdateCloseSession
        {
            private readonly LaunchIdentity game;
            private readonly uint appId;
            private readonly Action<string, bool> log;
            private readonly SteamLocator locator = new SteamLocator();
            private readonly SteamProgress progress = new SteamProgress();
            private readonly SteamProgress liveProgress = new SteamProgress();
            private readonly SteamCounterFreshness freshness = new SteamCounterFreshness();
            private readonly SteamTransferMetrics metrics = new SteamTransferMetrics();
            private SteamLiveSample lastLive;
            private UpdateObservation lastLiveObservation;
            private string lastLiveStage;
            private bool lastLivePaused;
            private string root, manifestPath;
            private SteamContentLog content;
            private SteamLiveReader live;
            private readonly Func<SteamLiveSample> sharedRead;
            private SteamUpdateControl control;
            private SteamQueueReader closeReader;
            private DateTime? closeAcknowledgedUtc;
            private readonly object sessionGate = new object();
            private bool disposed;
            private bool sawSteam, knownUpdating, usedLive;
            private DateTime lastSearch = DateTime.MinValue, absentSince = DateTime.MinValue;
            public Session(LaunchIdentity game, Action<string, bool> log, string steamRoot = null, Func<SteamLiveSample> sharedRead = null)
            {
                this.game = game;
                this.log = log;
                appId = uint.Parse(game.LauncherId);
                root = steamRoot;
                this.sharedRead = sharedRead;
            }
            public UpdateObservation Poll()
            {
                UpdateObservation observed;
                if (sharedRead != null)
                {
                    SteamLiveSample sample;
                    lock (sessionGate)
                    {
                        if (disposed) return new UpdateObservation { Phase = UpdatePhase.Idle };
                        sample = sharedRead();
                    }
                    // Borrowed data has the same identity/freshness rules as the reader.
                    var now = DateTime.UtcNow;
                    observed = sample != null && sample.AppId == appId && sample.ReceivedAtUtc <= now &&
                        now - sample.ReceivedAtUtc <= TimeSpan.FromSeconds(5)
                        ? ObserveLiveSample(sample)
                        : new UpdateObservation
                        {
                            Phase = UpdatePhase.Updating,
                            Display = new UpdateDisplay { Stage = lastLiveStage, Paused = lastLivePaused, WaitingForData = true },
                            Detail = "Waiting for fresh selected-update data"
                        };
                }
                else observed = PollProgress();
                lock (sessionGate) { if (!disposed && closeReader != null && closeAcknowledgedUtc.HasValue) observed.CloseConfirmed = SteamQueueClosePolicy.Confirmed(closeReader.TryRead(), appId, closeAcknowledgedUtc.Value); }
                return observed;
            }
            private UpdateObservation PollProgress()
            {
                if (SteamLocator.IsAppRunning(appId)) return new UpdateObservation { Phase = UpdatePhase.Running, Detail = "Steam registry reports running" };
                bool steamRunning = SteamLocator.IsSteamRunning();
                if (steamRunning) { sawSteam = true; absentSince = DateTime.MinValue; }
                else if (sawSteam)
                {
                    if (absentSince == DateTime.MinValue) absentSince = DateTime.UtcNow;
                    if ((DateTime.UtcNow - absentSince).TotalSeconds >= 30) return new UpdateObservation { Phase = UpdatePhase.Idle, Detail = "Steam exited" };
                }
                if ((root == null || manifestPath == null || !File.Exists(manifestPath)) && (DateTime.UtcNow - lastSearch).TotalSeconds >= 2)
                {
                    lastSearch = DateTime.UtcNow;
                    root = locator.FindRoot();
                    if (root != null)
                    {
                        if (content == null) content = new SteamContentLog(Path.Combine(root, "logs", "content_log.txt"), appId);
                        manifestPath = locator.FindManifest(root, appId, game.InstallDirectory);
                        if (manifestPath != null) log("AppID " + appId + " library=" + Directory.GetParent(Path.GetDirectoryName(manifestPath)).FullName + " manifest=" + manifestPath, false);
                    }
                }
                if (content != null) content.Poll();
                if (root != null && steamRunning)
                {
                    SteamLiveSample sample;
                    lock (sessionGate)
                    {
                        if (disposed) return new UpdateObservation { Phase = UpdatePhase.Idle };
                        if (live == null) live = new SteamLiveReader(root, appId, log);
                        sample = live.TryRead();
                    }
                    if (sample != null)
                    {
                        return ObserveLiveSample(sample);
                    }
                    if (usedLive && knownUpdating)
                    {
                        return new UpdateObservation
                        {
                            Phase = UpdatePhase.Updating,
                            Display = new UpdateDisplay { Stage = lastLiveStage, Paused = lastLivePaused, WaitingForData = true },
                            Detail = "Live Steam reader unavailable; suppressing stale checkpoint percentage"
                        };
                    }
                }
                if (manifestPath == null || !File.Exists(manifestPath))
                    return new UpdateObservation { Phase = content != null && content.Active ? UpdatePhase.Updating : UpdatePhase.Launching, Detail = "Manifest not yet available" };
                try
                {
                    var manifest = SteamManifestParser.Parse(SteamManifestParser.ReadShared(manifestPath), appId);
                    bool freshCounters = freshness.Observe(manifest, DateTime.UtcNow);
                    if (content != null && content.Error != null)
                        return new UpdateObservation { Phase = UpdatePhase.Error, Detail = content.Error };
                    var observed = progress.Evaluate(manifest, content != null && content.Active, freshCounters, null);
                    knownUpdating = observed.Phase == UpdatePhase.Updating;
                    if (knownUpdating && !freshCounters)
                    {
                        observed.Detail = "Waiting for fresh Steam counters (manifest checkpoint is not live progress)";
                        if (observed.Display != null) observed.Display.WaitingForData = !observed.Display.Paused;
                    }
                    if (observed.Display != null && live != null && live.AvailabilityNotice != null) observed.Display.Notice = live.AvailabilityNotice;
                    if (!knownUpdating) freshness.Reset();
                    return observed;
                }
                catch (IOException) { return Unreadable(); }
                catch (UnauthorizedAccessException) { return Unreadable(); }
                catch (FormatException) { return Unreadable(); }
            }
            private UpdateObservation ObserveLiveSample(SteamLiveSample sample)
            {
                if (object.ReferenceEquals(sample, lastLive)) return lastLiveObservation;
                if (!usedLive) { usedLive = true; log("Using Steam UI counters for AppID " + appId, false); }
                var measured = sample.ToManifest();
                string error = sample.HasCounters && sample.Result >= 5 && measured.Updating ? "Update error " + sample.Result : null;
                // Live install flags outrank delayed manifest/log checkpoints.
                var observed = liveProgress.Evaluate(measured, sample.UpdateState != 0, sample.HasCounters, error);
                if (sample.HasCounters && sample.DownloadsEnabled.HasValue)
                { observed.ControlPaused = !sample.DownloadsEnabled.Value; observed.ControlScope = "Steam downloads"; }
                if (observed.Phase == UpdatePhase.Updating && !observed.CompletionPending)
                {
                    observed.Display = SteamLivePresentation.Build(sample, lastLiveStage);
                    metrics.Apply(sample, observed.Display);
                    if (sample.DownloadsEnabled == false)
                    {
                        observed.Display.BytesPerSecond = null; observed.Display.SecondsRemaining = null;
                        observed.Display.Notice = "Download paused";
                    }
                    observed.Percent = observed.Display.WorkPercent;
                }
                if (observed.Display != null) { lastLiveStage = observed.Display.Stage; lastLivePaused = observed.Display.Paused; }
                if (observed.Phase == UpdatePhase.Updating && !observed.CompletionPending && sample.HasCounters && observed.Display != null &&
                    !observed.Display.WaitingForData && sample.DownloadsEnabled.HasValue && SteamControlPolicy.Available(sample.InstallState, sample.UpdateState, sample.Result, sample.HasCounters))
                {
                    lock (sessionGate)
                    {
                        if (!disposed && control == null) control = new SteamUpdateControl(root, appId, log);
                        if (!disposed && control.Supported)
                        {
                            observed.ControlPaused = !sample.DownloadsEnabled.Value; observed.ControlScope = "Steam downloads";
                            observed.CanPause = sample.DownloadsEnabled.Value; observed.CanResume = !sample.DownloadsEnabled.Value;
                        }
                    }
                }
                observed.ReceivedAtUtc = SteamSelectionPolicy.Known(sample.InstallState) && sample.DownloadsEnabled.HasValue
                    ? (DateTime?)sample.ReceivedAtUtc : null;
                observed.Detail = error ?? "Live Steam IPC";
                knownUpdating = observed.Phase == UpdatePhase.Updating;
                lastLive = sample; lastLiveObservation = observed;
                return observed;
            }

            private UpdateObservation Unreadable()
            { return new UpdateObservation { Phase = knownUpdating || content != null && content.Active ? UpdatePhase.Updating : UpdatePhase.Launching, Detail = "Manifest temporarily unreadable" }; }
            public bool SendCommand(UpdateCommand command)
            {
                SteamUpdateControl sender;
                lock (sessionGate) { if (disposed) return false; sender = control; }
                return sender != null && sender.Send(command);
            }
            public bool SendClose()
            {
                SteamUpdateControl sender;
                lock (sessionGate)
                {
                    if (disposed) return false;
                    if (root == null) root = locator.FindRoot();
                    if (root == null) return false;
                    if (control == null) control = new SteamUpdateControl(root, appId, log);
                    if (!control.Supported) return false;
                    if (closeReader == null) closeReader = new SteamQueueReader(root, new[] { appId }, log);
                    sender = control;
                }
                bool accepted = sender.CloseUpdate();
                lock (sessionGate) { if (!disposed) closeAcknowledgedUtc = accepted ? (DateTime?)DateTime.UtcNow : null; }
                return accepted;
            }
            public void Dispose() { lock (sessionGate) { disposed = true; if (live != null) live.Dispose(); if (closeReader != null) closeReader.Dispose(); if (control != null) control.Dispose(); } }
        }
    }
}
