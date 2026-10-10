using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

namespace PlayniteUpdateStatus.Launch
{
    // Owns SDK startup retention, cancellation acknowledgements and replay.
    // The plugin forwards SDK events; UI code only presents the current state.
    internal sealed class LaunchCoordinator : IDisposable
    {
        private readonly IPlayniteAPI api;
        private readonly UpdateMonitor monitor;
        private LauncherRegistry launchers;
        private readonly SteamUpdateCatalog catalog;
        private readonly Func<Settings> settings;
        private readonly ILogger logger;
        private volatile bool stopped;
        private bool disposed;
        private Game activeGame;
        private LauncherMatch activeLauncher;
        private bool observedUpdate;
        private long returnedToLibrarySession = -1;

        private SteamLaunchRecovery steamRecovery;
        private long recoverySession;
        private SelectedLaunchGate selectedLaunch;
        private readonly object selectionGate = new object();
        private long launchRequest;
        private readonly Dictionary<Guid, Queue<long>> ownStartupCancellations = new Dictionary<Guid, Queue<long>>();
        private readonly HashSet<long> heldSessions = new HashSet<long>();
        private readonly HashSet<long> sdkCancelledSessions = new HashSet<long>();
        private readonly HashSet<long> closedHeldSessions = new HashSet<long>();
        // UI callbacks must unwind before another retained UI startup enters.
        // Tokens remain here until the host acknowledges its own cancellation.
        private readonly Dictionary<long, Guid> uiStartupCleanup = new Dictionary<long, Guid>();
        private Func<LaunchIdentity, SteamCatalogSnapshot, bool> selectionPreflight = SteamSelectionPreflight.ShouldDefer;
        private Func<LaunchIdentity, IUpdateControlSession> selectedSessionFactory;
        private Func<uint, Func<bool>, Action<string, bool>, SteamLaunchRecovery> recoveryFactory = SteamLaunchRecovery.Create;

        private sealed class PausedLaunch
        {
            public Guid GameId;
            public long SessionId, Request;
            public bool SdkCancelled, ResumeConfirmed, RestartScheduled;
        }
        private PausedLaunch pausedLaunch, queuedResume;

        private sealed class DeferredPlay
        {
            public Guid GameId, PreviousGameId;
            public long Request, PreviousSession;
            public Task<bool> Removal;
            public readonly HashSet<Guid> CleanupGames = new HashSet<Guid>();
            public DateTime? CleanupDeadline;
        }
        private DeferredPlay deferredPlay;
        private readonly Dictionary<Guid, Queue<DeferredPlay>> replacementReplays = new Dictionary<Guid, Queue<DeferredPlay>>();

        public Game ActiveGame
        {
            get
            {
                return activeGame;
            }
        }
        public LauncherMatch ActiveLauncher
        {
            get
            {
                return activeLauncher;
            }
        }
        public bool ObservedUpdate
        {
            get
            {
                return observedUpdate;
            }
        }
        private Settings Settings
        {
            get
            {
                return settings();
            }
        }

        public event Action MonitoringRequested;
        public event Action<bool> ObservationStarted;
        public event Action StatusRequested;

        public LaunchCoordinator(IPlayniteAPI api, UpdateMonitor monitor, LauncherRegistry launchers,
            SteamUpdateCatalog catalog, Func<Settings> settings, ILogger logger)
        {
            this.api = api;
            this.monitor = monitor;
            this.launchers = launchers;
            this.catalog = catalog;
            this.settings = settings;
            this.logger = logger;
            selectedSessionFactory = identity => SteamSelectedUpdateSession.Open(identity, Log, () => catalog.Current);
            monitor.Changed += ObserveState;
            monitor.CommandRequested += CommandRequested;
            monitor.CommandConfirmed += CommandConfirmed;
            monitor.CloseRequested += UpdateCloseRequested;
        }

        private void Log(string message, bool warning)
        {
            if (warning)
                logger.Warn("[Update Status] " + message);
            else
                logger.Info("[Update Status] " + message);
        }

        private void RequestMonitoring()
        {
            catalog.Start();
            catalog.RefreshSoon();
            var requested = MonitoringRequested;
            if (requested != null)
                requested();
        }

        private void NotifyObservationStarted(bool resetForeground)
        {
            var started = ObservationStarted;
            if (started != null)
                started(resetForeground);
        }

        private void ObserveState(UpdateState state)
        {
            if (stopped)
                return;
            if (state.CancelPendingPlay)
            {
                lock (selectionGate)
                {
                    if (selectedLaunch != null && selectedLaunch.SessionId == state.SessionId && selectedLaunch.GameId == state.GameId)
                        selectedLaunch.Cancel();
                    if (pausedLaunch != null && pausedLaunch.SessionId == state.SessionId && pausedLaunch.GameId == state.GameId)
                    {
                        pausedLaunch.ResumeConfirmed = false;
                        pausedLaunch = null;
                    }
                    if (queuedResume != null && queuedResume.SessionId == state.SessionId && queuedResume.GameId == state.GameId)
                        queuedResume = null;
                }
            }
            if (!state.Visible)
            {
                lock (selectionGate)
                {
                    if (pausedLaunch != null)
                    {
                        pausedLaunch.ResumeConfirmed = false;
                        pausedLaunch = null;
                    }
                }
            }
            if (CanRetainResumedPlay(state))
                RestartPausedLaunch();
            var recovery = steamRecovery;
            if (recovery != null && (!state.Visible || state.SessionId == recoverySession))
                recovery.Observe(state, DateTime.UtcNow);
        }

        public void ClearObservation()
        {
            activeGame = null;
            activeLauncher = null;
            observedUpdate = false;
        }

        public void MarkUpdateObserved()
        {
            observedUpdate = true;
        }
        public void ResetLibraryReturn()
        {
            returnedToLibrarySession = -1;
        }

        public void PollRecovery(DateTime now)
        {
            var recovery = steamRecovery;
            if (!stopped && recovery != null && monitor.Current.SessionId == recoverySession)
                recovery.Poll(now);
        }

        // Called by the UI only after it accepts the current monitor revision.
        // The host acknowledges cancellation before library navigation occurs.
        public void Dismiss(UpdateState state)
        {
            if (stopped || returnedToLibrarySession == state.SessionId)
                return;
            returnedToLibrarySession = state.SessionId;
            bool held, navigate;
            lock (selectionGate)
            {
                held = heldSessions.Remove(state.SessionId);
                navigate = !held;
                if (held)
                {
                    closedHeldSessions.Add(state.SessionId);
                    if (sdkCancelledSessions.Remove(state.SessionId))
                    {
                        closedHeldSessions.Remove(state.SessionId);
                        navigate = true;
                    }
                }
            }
            if (monitor.Current.SessionId == state.SessionId)
                monitor.Stop(state.GameId);
            if (!held)
                Log("Closed update observer; forwarded launch remains owned by its launcher and Playnite", false);
            if (navigate)
                ReturnToLibrary(state.SessionId);
        }

        public void Stop()
        {
            lock (selectionGate)
            {
                if (stopped)
                    return;
                stopped = true;
                ++launchRequest;
                if (steamRecovery != null)
                    steamRecovery.Cancel();
                if (selectedLaunch != null)
                    selectedLaunch.Cancel();
                if (pausedLaunch != null)
                    pausedLaunch.ResumeConfirmed = false;
                pausedLaunch = queuedResume = null;
                deferredPlay = null;
            }
        }

        public void Dispose()
        {
            Stop();
            if (disposed)
                return;
            disposed = true;
            monitor.Changed -= ObserveState;
            monitor.CommandRequested -= CommandRequested;
            monitor.CommandConfirmed -= CommandConfirmed;
            monitor.CloseRequested -= UpdateCloseRequested;
        }

        private void UpdateCloseRequested(Guid id, long token)
        {
            var recovery = steamRecovery;
            if (recovery != null && token == recoverySession)
                recovery.Cancel();
            lock (selectionGate)
            {
                if (pausedLaunch != null && pausedLaunch.SessionId == token)
                {
                    pausedLaunch.ResumeConfirmed = false;
                    pausedLaunch = null;
                }
                deferredPlay = null;
                if (selectedLaunch != null && selectedLaunch.SessionId == token && selectedLaunch.GameId == id)
                    selectedLaunch.Cancel();
            }
        }
        public void OnGameStarting(OnGameStartingEventArgs args)
        {
            if (stopped)
            {
                args.CancelStartup = true;
                return;
            }
            bool held = false;
            SelectedLaunchGate ticket = null;
            long request;
            SelectedLaunchGate previous;
            bool resumedPlay = false;
            bool serializeUiPlay;
            lock (selectionGate)
            {
                if (!ConsumeReplacementReplay(args))
                    return;
                if (queuedResume != null && queuedResume.GameId == args.Game.Id)
                {
                    var resume = queuedResume;
                    queuedResume = null;
                    var state = monitor.Current;
                    if (!resume.ResumeConfirmed || !IsCurrentLaunch(resume.Request) || state.SessionId != resume.SessionId || !CanRetainResumedPlay(state))
                    {
                        if (pausedLaunch == resume)
                            resume.RestartScheduled = false;
                        CancelReplacementStartup(args, resume.Request);
                        return;
                    }
                    resumedPlay = true;
                }
                serializeUiPlay = !resumedPlay && api.MainView.UIDispatcher.CheckAccess() && (uiStartupCleanup.Count != 0 || deferredPlay != null);
                request = ++launchRequest;
                if (pausedLaunch != null)
                {
                    pausedLaunch.ResumeConfirmed = false;
                    pausedLaunch = null;
                }
                returnedToLibrarySession = -1;
                closedHeldSessions.Clear();
                sdkCancelledSessions.Clear();
                previous = selectedLaunch;
                if (previous != null)
                    previous.Cancel();
                if (steamRecovery != null)
                    steamRecovery.Cancel();
                steamRecovery = null;
            }
            if (!Settings.Enabled || args.CancelStartup)
                return;
            RequestMonitoring();
            try
            {
                if (resumedPlay)
                {
                    lock (selectionGate)
                    {
                        if (!IsCurrentLaunch(request))
                        {
                            CancelReplacementStartup(args, request);
                            return;
                        }
                        activeGame = args.Game;
                        NotifyObservationStarted(false);
                        ticket = RetainSelectedPlay(args, monitor.Current.SessionId);
                    }
                    held = true;
                    Log("Resume retains SDK Play while the selected update continues", false);
                    WaitForSelectedPlay(args, ticket, request);
                    return;
                }
                var old = monitor.Current;
                // Returning this short callback lets the cancelled outer UI Play
                // finish host cleanup. Replay only the supported native action;
                // never replace a custom action or selected ROM with SDK defaults.
                if (serializeUiPlay && IsNativeSteamPlay(args))
                {
                    DeferReplacementPlay(args, request, old, previous);
                    return;
                }
                lock (selectionGate)
                {
                    if (IsCurrentLaunch(request))
                        deferredPlay = null;
                }
                if (RequiresReplacementRemoval(old, args.Game.Id, previous))
                {
                    held = true;
                    Log("Removing previous update before replacement Play; previous=" + old.GameId + "; next=" + args.Game.Id, false);
                    var removal = monitor.RemoveForReplacement(old.GameId, old.SessionId);
                    if (!ReplacementLaunchWait.Wait(removal, api.MainView.UIDispatcher, () => IsCurrentLaunch(request)))
                    {
                        CancelReplacementStartup(args, request);
                        return;
                    }
                }
                var match = launchers.Resolve(args.Game, null);
                if (match == null)
                {
                    lock (selectionGate)
                    {
                        if (IsCurrentLaunch(request))
                        {
                            heldSessions.Clear();
                            sdkCancelledSessions.Clear();
                            monitor.Stop(Guid.Empty);
                        }
                        else
                            CancelReplacementStartup(args, request);
                    }
                    return;
                }
                var identity = match.Identity;
                Log("Launching game=" + identity.Name + " PlayniteId=" + identity.GameId + " launcher=" + identity.LauncherName + " nativeId=" + identity.LauncherId, false);
                // Custom actions can contain scripts/options that cannot safely be replayed.
                // Capture only the native Steam-library request, before its controller runs.
                bool nativeSteamPlay = match.Registration.Key == "steam" && IsNativeSteamPlay(args);
                bool defer = nativeSteamPlay && selectionPreflight(identity, catalog.Current);
                lock (selectionGate)
                {
                    if (!IsCurrentLaunch(request))
                    {
                        CancelReplacementStartup(args, request);
                        return;
                    }
                    held = defer;
                    heldSessions.Clear();
                    sdkCancelledSessions.Clear();
                    activeLauncher = match;
                    activeGame = args.Game;
                    observedUpdate = false;
                    NotifyObservationStarted(true);
                    var recovery = nativeSteamPlay && !defer ? recoveryFactory(uint.Parse(identity.LauncherId), () => args.Game.IsRunning, Log) : null;
                    var starting = monitor.Start(identity, defer ? selectedSessionFactory(identity) : match.Registration.Adapter.Open(identity), Settings.PollingInterval);
                    recoverySession = starting.SessionId;
                    steamRecovery = recovery;
                    if (recovery != null)
                        recovery.Observe(monitor.Current, DateTime.UtcNow);
                    if (defer)
                        ticket = RetainSelectedPlay(args, starting.SessionId);
                }
                var statusRequested = StatusRequested;
                if (statusRequested != null)
                    statusRequested();
                if (defer)
                {
                    observedUpdate = true;
                    Log("Holding original Play for selected update AppID=" + identity.LauncherId + "; no native game launch sent", false);
                    WaitForSelectedPlay(args, ticket, request);
                }
            }
            catch (Exception e)
            {
                if (held)
                {
                    if (ticket == null)
                        CancelReplacementStartup(args, request);
                    else
                        args.CancelStartup = true;
                }
                logger.Error(e, held ? "[Update Status] Selected update failed; original Play cancelled." : "[Update Status] Failed to start observer; normal launch continues.");
                lock (selectionGate)
                {
                    if (IsCurrentLaunch(request))
                        monitor.Stop(args.Game.Id);
                }
            }
        }
        private static bool IsNativeSteamPlay(OnGameStartingEventArgs args)
        {
            return args.Game.PluginId == SteamGameIdentity.LibraryId && args.SourceAction == null && string.IsNullOrEmpty(args.SelectedRomFile);
        }
        private SelectedLaunchGate RetainSelectedPlay(OnGameStartingEventArgs args, long sessionId)
        {
            var ticket = new SelectedLaunchGate(args, sessionId);
            selectedLaunch = ticket;
            heldSessions.Add(sessionId);
            if (api.MainView.UIDispatcher.CheckAccess())
                uiStartupCleanup[sessionId] = args.Game.Id;
            return ticket;
        }
        private void WaitForSelectedPlay(OnGameStartingEventArgs args, SelectedLaunchGate ticket, long request)
        {
            bool release = false;
            try
            {
                release = ticket.Wait(monitor, api.MainView.UIDispatcher);
            }
            finally
            {
                lock (selectionGate)
                {
                    release &= !args.CancelStartup && IsCurrentLaunch(request);
                    if (!release)
                    {
                        args.CancelStartup = true;
                        RecordStartupCancellation(args.Game.Id, ticket.SessionId);
                    }
                    else
                        uiStartupCleanup.Remove(ticket.SessionId);
                    if (selectedLaunch == ticket)
                        selectedLaunch = null;
                    if (release || monitor.Current.SessionId != ticket.SessionId)
                        heldSessions.Remove(ticket.SessionId);
                }
            }
            Log(release ? "Selected update complete; releasing SDK Play" : "Discarded held SDK Play", false);
        }
        private bool IsCurrentLaunch(long request)
        {
            lock (selectionGate)
                return !stopped && Settings.Enabled && request == launchRequest;
        }
        private void CancelReplacementStartup(OnGameStartingEventArgs args, long request)
        {
            args.CancelStartup = true;
            lock (selectionGate)
            {
                // No monitor session was created for this startup. A negative
                // request token keeps its delayed SDK acknowledgement separate.
                RecordStartupCancellation(args.Game.Id, -request);
            }
        }
        // Call while holding selectionGate, before the SDK acknowledges cancellation.
        private void RecordStartupCancellation(Guid gameId, long token)
        {
            Queue<long> cancellations;
            if (!ownStartupCancellations.TryGetValue(gameId, out cancellations))
                ownStartupCancellations[gameId] = cancellations = new Queue<long>();
            cancellations.Enqueue(token);
        }
        public void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (stopped)
                return;
            Log("Game started " + args.Game.Id, false);
            monitor.Stop(args.Game.Id);
        }
        public void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (!stopped)
                monitor.Stop(args.Game.Id);
        }
        public void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            if (stopped)
                return;
            Log("Launch cancelled " + args.Game.Id, false);
            long cancelledSession = 0;
            bool navigate = false, restart = false;
            lock (selectionGate)
            {
                Queue<long> cancellations;
                if (ownStartupCancellations.TryGetValue(args.Game.Id, out cancellations))
                {
                    cancelledSession = cancellations.Dequeue();
                    uiStartupCleanup.Remove(cancelledSession);
                    if (cancellations.Count == 0)
                        ownStartupCancellations.Remove(args.Game.Id);
                    if (pausedLaunch != null && pausedLaunch.SessionId == cancelledSession)
                    {
                        pausedLaunch.SdkCancelled = true;
                        heldSessions.Remove(cancelledSession);
                        restart = true;
                    }
                    else if (closedHeldSessions.Remove(cancelledSession))
                        navigate = true;
                    else if (heldSessions.Contains(cancelledSession))
                        sdkCancelledSessions.Add(cancelledSession);
                }
            }
            if (cancelledSession != 0)
            {
                if (restart)
                    RestartPausedLaunch();
                if (navigate)
                    ReturnToLibrary(cancelledSession);
                return;
            }
            monitor.Stop(args.Game.Id);
        }
        private void ReturnToLibrary(long sessionId)
        {
            var dispatcher = api.MainView.UIDispatcher;
            if (dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (stopped || returnedToLibrarySession != sessionId || monitor.Current.Visible && monitor.Current.SessionId != sessionId)
                    return;
                try
                {
                    LibraryNavigation.Return(api);
                }
                catch (Exception e) { Log("Library return failed: " + e.Message, false); }
            }));
        }
        private void CommandRequested(Guid id, long token, UpdateCommand command)
        {
            if (stopped)
                return;
            var recovery = steamRecovery;
            if (recovery != null && token == recoverySession)
                recovery.CommandRequested(command);
            if (command != UpdateCommand.Pause)
                return;
            lock (selectionGate)
            {
                if (pausedLaunch != null && pausedLaunch.GameId == id && pausedLaunch.SessionId == token)
                {
                    pausedLaunch.ResumeConfirmed = false;
                    return;
                }
                var ticket = selectedLaunch;
                if (ticket == null || ticket.GameId != id || ticket.SessionId != token)
                    return;
                pausedLaunch = new PausedLaunch { GameId = id, SessionId = token, Request = launchRequest };
                ticket.Cancel();
                Log("Pause cancels the held SDK startup; update observation remains available", false);
            }
        }
        private void CommandConfirmed(Guid id, long token, UpdateCommand command)
        {
            if (stopped)
                return;
            var recovery = steamRecovery;
            if (recovery != null && token == recoverySession)
                recovery.CommandConfirmed(command);
            if (command != UpdateCommand.Resume)
                return;
            lock (selectionGate)
            {
                if (pausedLaunch == null || pausedLaunch.GameId != id || pausedLaunch.SessionId != token)
                    return;
                pausedLaunch.ResumeConfirmed = true;
            }
            Log("Resume restores selected SDK Play after cancellation cleanup", false);
            RestartPausedLaunch();
        }
        private void RestartPausedLaunch()
        {
            PausedLaunch candidate;
            var dispatcher = api.MainView.UIDispatcher;
            lock (selectionGate)
            {
                candidate = pausedLaunch;
                if (candidate == null || !candidate.SdkCancelled || !candidate.ResumeConfirmed || candidate.RestartScheduled || !IsCurrentLaunch(candidate.Request))
                    return;
                var state = monitor.Current;
                if (state.GameId != candidate.GameId || state.SessionId != candidate.SessionId || !CanRetainResumedPlay(state))
                    return;
                candidate.RestartScheduled = true;
            }
            if (dispatcher.HasShutdownStarted)
                return;
            DispatchPausedRestart(candidate, DateTime.UtcNow.AddSeconds(3));
        }
        private void DispatchPausedRestart(PausedLaunch candidate, DateTime deadline)
        {
            var dispatcher = api.MainView.UIDispatcher;
            if (dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                lock (selectionGate)
                {
                    var state = monitor.Current;
                    if (pausedLaunch != candidate || !candidate.ResumeConfirmed || !IsCurrentLaunch(candidate.Request) || state.SessionId != candidate.SessionId || !CanRetainResumedPlay(state))
                    {
                        if (pausedLaunch == candidate)
                            candidate.RestartScheduled = false;
                        return;
                    }
                    if (activeGame == null || activeGame.Id != candidate.GameId)
                    {
                        candidate.RestartScheduled = false;
                        return;
                    }
                    // Starting/cancelled SDK events contain snapshots. Their flags
                    // do not change when the host clears the actual database game.
                    Playnite.SDK.Models.Game liveGame;
                    try
                    {
                        liveGame = api.Database.Games.Get(candidate.GameId);
                    }
                    catch (Exception e) { candidate.RestartScheduled = false; monitor.ReportNotice(candidate.GameId, candidate.SessionId, "Unable to restart — press Play again"); Log("Resume game lookup unavailable: " + e.Message, false); return; }
                    if (liveGame == null || liveGame.Id != candidate.GameId)
                    {
                        candidate.RestartScheduled = false;
                        monitor.ReportNotice(candidate.GameId, candidate.SessionId, "Unable to restart — press Play again");
                        Log("Resume game no longer available in Playnite database", false);
                        return;
                    }
                    if (liveGame.IsRunning)
                    {
                        candidate.ResumeConfirmed = false;
                        candidate.RestartScheduled = false;
                        return;
                    }
                    // Some hosts clear IsLaunching after their cancellation event.
                    // Wait for current database state; never edit execution flags.
                    if (liveGame.IsLaunching)
                    {
                        if (DateTime.UtcNow > deadline)
                        {
                            candidate.RestartScheduled = false;
                            monitor.ReportNotice(candidate.GameId, candidate.SessionId, "Unable to restart — press Play again");
                            Log("Resume startup still tracked by Playnite; press Play again after cleanup", false);
                            return;
                        }
                        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
                        timer.Tick += (s, e) => { timer.Stop(); DispatchPausedRestart(candidate, deadline); };
                        timer.Start();
                        return;
                    }
                    // The public SDK owns the new startup/controller/scripts. A
                    // late callback is checked again before replacing observation.
                    queuedResume = candidate;
                }
                try
                {
                    api.StartGame(candidate.GameId);
                    Log("Resume requested a new SDK startup for the paused selection", false);
                }
                catch (Exception e) { lock (selectionGate) { candidate.ResumeConfirmed = false; candidate.RestartScheduled = false; if (queuedResume == candidate) queuedResume = null; } monitor.ReportNotice(candidate.GameId, candidate.SessionId, "Unable to restart — press Play again"); Log("Resume startup unavailable; press Play again: " + e.Message, false); }
            }));
        }
        private static bool CanRetainResumedPlay(UpdateState state)
        {
            return (state.Phase == UpdatePhase.Updating || state.Phase == UpdatePhase.Ready) && !state.Dismissed && !state.CommandPending && state.ControlPaused == false;
        }
        // Called under selectionGate. Public SDK callbacks are synchronous on the
        // current host, but a delayed callback must still reject a superseded Play.
        private bool ConsumeReplacementReplay(OnGameStartingEventArgs args)
        {
            Queue<DeferredPlay> replays;
            if (!replacementReplays.TryGetValue(args.Game.Id, out replays))
                return true;
            var replay = replays.Dequeue();
            if (replays.Count == 0)
                replacementReplays.Remove(args.Game.Id);
            var state = monitor.Current;
            if (IsCurrentLaunch(replay.Request) && state.SessionId == replay.PreviousSession && !state.Dismissed)
                return true;
            CancelReplacementStartup(args, replay.Request);
            return false;
        }
        private bool RequiresReplacementRemoval(UpdateState previousState, Guid nextGameId, SelectedLaunchGate previousPlay)
        {
            return previousState.Visible && previousState.GameId != nextGameId && monitor.CanRemoveForReplacement &&
                (previousState.Phase == UpdatePhase.Updating ||
                 previousState.Phase == UpdatePhase.Error && observedUpdate ||
                 previousState.CommandPending ||
                 previousPlay != null && previousPlay.SessionId == previousState.SessionId);
        }

        private void DeferReplacementPlay(OnGameStartingEventArgs args, long request, UpdateState old, SelectedLaunchGate previous)
        {
            var pending = new DeferredPlay { GameId = args.Game.Id, PreviousGameId = old.GameId, PreviousSession = old.SessionId, Request = request };
            lock (selectionGate)
            {
                if (!IsCurrentLaunch(request))
                {
                    CancelReplacementStartup(args, request);
                    return;
                }
                foreach (var gameId in uiStartupCleanup.Values)
                    pending.CleanupGames.Add(gameId);
                if (deferredPlay != null)
                    pending.CleanupGames.UnionWith(deferredPlay.CleanupGames);
                pending.CleanupGames.Add(args.Game.Id);
                deferredPlay = pending;
                CancelReplacementStartup(args, request);
                uiStartupCleanup[-request] = args.Game.Id;
            }
            bool remove = RequiresReplacementRemoval(old, args.Game.Id, previous);
            pending.Removal = remove ? monitor.RemoveForReplacement(old.GameId, old.SessionId) : Task.FromResult(true);
            Log("Replacement Play waits for previous UI startup cleanup", false);
            var dispatcher = api.MainView.UIDispatcher;
            if (dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
                timer.Tick += (s, e) => { if (TryDispatchReplacement(pending)) timer.Stop(); };
                if (!TryDispatchReplacement(pending))
                    timer.Start();
            }));
        }
        // True means this timer is finished, including cancellation or refusal.
        private bool TryDispatchReplacement(DeferredPlay pending)
        {
            var dispatcher = api.MainView.UIDispatcher;
            lock (selectionGate)
            {
                if (dispatcher.HasShutdownStarted || deferredPlay != pending || !IsCurrentLaunch(pending.Request))
                    return true;
                var state = monitor.Current;
                if (state.SessionId != pending.PreviousSession || state.Dismissed)
                {
                    deferredPlay = null;
                    return true;
                }
                if (!pending.Removal.IsCompleted)
                    return false;
                if (pending.Removal.Status != TaskStatus.RanToCompletion || !pending.Removal.Result)
                {
                    deferredPlay = null;
                    Log("Replacement Play cancelled: previous update removal was not confirmed", false);
                    return true;
                }
                if (!pending.CleanupDeadline.HasValue)
                    pending.CleanupDeadline = DateTime.UtcNow.AddSeconds(3);
                bool waiting = uiStartupCleanup.Count != 0;
                try
                {
                    foreach (var id in pending.CleanupGames)
                    {
                        var game = api.Database.Games.Get(id);
                        if (game == null || game.Id != id)
                            throw new InvalidOperationException("Startup game unavailable");
                        if (id == pending.GameId && game.IsRunning)
                        {
                            deferredPlay = null;
                            return true;
                        }
                        waiting |= game.IsLaunching;
                    }
                }
                catch (Exception e) { ReplacementUnavailable(pending, e.Message); return true; }
                if (waiting)
                {
                    if (DateTime.UtcNow > pending.CleanupDeadline.Value)
                    {
                        ReplacementUnavailable(pending, "Host cancellation cleanup not confirmed");
                        return true;
                    }
                    return false;
                }
                deferredPlay = null;
                Queue<DeferredPlay> replays;
                if (!replacementReplays.TryGetValue(pending.GameId, out replays))
                    replacementReplays[pending.GameId] = replays = new Queue<DeferredPlay>();
                replays.Enqueue(pending);
            }
            try
            {
                api.StartGame(pending.GameId);
            }
            catch (Exception e)
            {
                lock (selectionGate)
                {
                    Queue<DeferredPlay> replays;
                    if (replacementReplays.TryGetValue(pending.GameId, out replays) && replays.Count != 0 && replays.Peek() == pending)
                    {
                        replays.Dequeue();
                        if (replays.Count == 0)
                            replacementReplays.Remove(pending.GameId);
                    }
                }
                ReplacementUnavailable(pending, e.Message);
            }
            return true;
        }
        private void ReplacementUnavailable(DeferredPlay pending, string reason)
        {
            if (deferredPlay == pending)
                deferredPlay = null;
            monitor.ReportNotice(pending.PreviousGameId, pending.PreviousSession, "Unable to restart — press Play again");
            Log("Replacement SDK startup unavailable: " + reason, false);
        }
    }
}
