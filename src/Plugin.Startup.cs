using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Playnite.SDK.Events;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        private SteamLaunchRecovery steamRecovery;
        private long recoverySession;
        private SelectedLaunchGate selectedLaunch;
        private readonly object selectionGate = new object();
        private long launchRequest;
        private readonly Dictionary<Guid,Queue<long>> ownStartupCancellations = new Dictionary<Guid,Queue<long>>();
        private readonly HashSet<long> heldSessions = new HashSet<long>();
        private readonly HashSet<long> sdkCancelledSessions = new HashSet<long>();
        private readonly HashSet<long> closedHeldSessions = new HashSet<long>();
        // UI callbacks must unwind before another retained UI startup enters.
        // Tokens remain here until the host acknowledges its own cancellation.
        private readonly Dictionary<long,Guid> uiStartupCleanup = new Dictionary<long,Guid>();
        private Func<LaunchIdentity,SteamCatalogSnapshot,bool> selectionPreflight = SteamSelectionPreflight.ShouldDefer;
        private Func<LaunchIdentity,IUpdateControlSession> selectedSessionFactory;
        private Func<uint,Func<bool>,Action<string,bool>,SteamLaunchRecovery> recoveryFactory = SteamLaunchRecovery.Create;
        private void UpdateCloseRequested(Guid id, long token)
        {
            var recovery = steamRecovery;
            if (recovery != null && token == recoverySession) recovery.Cancel();
            lock (selectionGate)
            {
                if (pausedLaunch != null && pausedLaunch.SessionId == token) { pausedLaunch.ResumeConfirmed = false; pausedLaunch = null; }
                deferredPlay = null;
                if (selectedLaunch != null && selectedLaunch.SessionId == token && selectedLaunch.GameId == id) selectedLaunch.DiscardPlay();
            }
        }
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            if (closing || stopped) { args.CancelStartup = true; return; }
            bool held = false;
            SelectedLaunchGate ticket = null;
            long request;
            SelectedLaunchGate previous;
            bool resumedPlay = false;
            bool serializeUiPlay;
            lock (selectionGate) {
                if (!ConsumeReplacementReplay(args)) return;
                if (queuedResume != null && queuedResume.GameId == args.Game.Id)
                {
                    var resume = queuedResume; queuedResume = null; var state = Monitor.Current;
                    if (!resume.ResumeConfirmed || !IsCurrentLaunch(resume.Request) || state.SessionId != resume.SessionId || !CanRetainResumedPlay(state))
                    { if (pausedLaunch == resume) resume.RestartScheduled = false; CancelReplacementStartup(args,resume.Request); return; }
                    resumedPlay = true;
                }
                serializeUiPlay = !resumedPlay && PlayniteApi.MainView.UIDispatcher.CheckAccess() && (uiStartupCleanup.Count != 0 || deferredPlay != null);
                request = ++launchRequest;
                if (pausedLaunch != null) { pausedLaunch.ResumeConfirmed = false; pausedLaunch = null; }
                returnedToLibrarySession = -1; closedHeldSessions.Clear(); sdkCancelledSessions.Clear(); previous = selectedLaunch; if (previous != null) previous.Cancel();
                if (steamRecovery != null) steamRecovery.Cancel(); steamRecovery = null;
            }
            if (!Settings.Enabled || args.CancelStartup) return;
            SteamCatalog.Start(); SteamCatalog.RefreshSoon(); ApplyCoverSettings();
            try
            {
                if (resumedPlay)
                {
                    lock (selectionGate)
                    {
                        if (!IsCurrentLaunch(request)) { CancelReplacementStartup(args,request); return; }
                        activeGame = args.Game; foregroundResult = null;
                        ticket = RetainSelectedPlay(args,Monitor.Current.SessionId);
                    }
                    held = true;
                    Log("Resume retains SDK Play while the selected update continues",false);
                    WaitForSelectedPlay(args,ticket,request);
                    return;
                }
                var old = Monitor.Current;
                // Returning this short callback lets the cancelled outer UI Play
                // finish host cleanup. Replay only the supported native action;
                // never replace a custom action or selected ROM with SDK defaults.
                if (serializeUiPlay && IsNativeSteamPlay(args))
                { DeferReplacementPlay(args,request,old,previous); return; }
                lock (selectionGate) { if (IsCurrentLaunch(request)) deferredPlay = null; }
                if (old.Visible && old.GameId != args.Game.Id && Monitor.CanRemoveForReplacement &&
                    (old.Phase == UpdatePhase.Updating || old.Phase == UpdatePhase.Error && observedUpdate || old.CommandPending || previous != null && previous.SessionId == old.SessionId))
                {
                    held = true;
                    Log("Removing previous update before replacement Play; previous=" + old.GameId + "; next=" + args.Game.Id,false);
                    var removal = Monitor.RemoveForReplacement(old.GameId,old.SessionId);
                    if (!ReplacementLaunchWait.Wait(removal,PlayniteApi.MainView.UIDispatcher,() => IsCurrentLaunch(request)))
                    { CancelReplacementStartup(args,request); return; }
                }
                var match = launchers.Resolve(args.Game,null);
                if (match == null) { lock (selectionGate) { if (IsCurrentLaunch(request)) { heldSessions.Clear(); sdkCancelledSessions.Clear(); Monitor.Stop(Guid.Empty); } else CancelReplacementStartup(args,request); } return; }
                var identity = match.Identity;
                Log("Launching game=" + identity.Name + " PlayniteId=" + identity.GameId + " launcher=" + identity.LauncherName + " nativeId=" + identity.LauncherId, false);
                // Custom actions can contain scripts/options that cannot safely be replayed.
                // Capture only the native Steam-library request, before its controller runs.
                bool nativeSteamPlay = match.Registration.Key == "steam" && IsNativeSteamPlay(args);
                bool defer = nativeSteamPlay && selectionPreflight(identity,SteamCatalog.Current);
                lock (selectionGate)
                {
                    if (!IsCurrentLaunch(request)) { CancelReplacementStartup(args,request); return; }
                    held = defer;
                    heldSessions.Clear(); sdkCancelledSessions.Clear();
                    activeLauncher = match; activeGame = args.Game; foregroundResult = null; observedUpdate = false; foregroundPolicy.Reset();
                    var recovery = nativeSteamPlay && !defer ? recoveryFactory(uint.Parse(identity.LauncherId),() => args.Game.IsRunning,Log) : null;
                    var starting = Monitor.Start(identity, defer ? selectedSessionFactory(identity) : match.Registration.Adapter.Open(identity), Settings.PollingInterval);
                    recoverySession = starting.SessionId; steamRecovery = recovery;
                    if (recovery != null) recovery.Observe(Monitor.Current,DateTime.UtcNow);
                    if (defer) ticket = RetainSelectedPlay(args,starting.SessionId);
                }
                PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() => { if (!closing && !stopped && Monitor.Current.Visible) uiTimer.Start(); }));
                if (defer)
                {
                    observedUpdate = true;
                    Log("Holding original Play for selected update AppID=" + identity.LauncherId + "; no native game launch sent",false);
                    WaitForSelectedPlay(args,ticket,request);
                }
            }
            catch (Exception e)
            {
                if (held) { if (ticket == null) CancelReplacementStartup(args,request); else args.CancelStartup = true; }
                logger.Error(e, held ? "[Update Status] Selected update failed; original Play cancelled." : "[Update Status] Failed to start observer; normal launch continues.");
                lock (selectionGate) { if (IsCurrentLaunch(request)) Monitor.Stop(args.Game.Id); }
            }
        }
        private static bool IsNativeSteamPlay(OnGameStartingEventArgs args)
        { return args.Game.PluginId == SteamGameIdentity.LibraryId && args.SourceAction == null && string.IsNullOrEmpty(args.SelectedRomFile); }
        private SelectedLaunchGate RetainSelectedPlay(OnGameStartingEventArgs args, long sessionId)
        {
            var ticket = new SelectedLaunchGate(args,sessionId);
            selectedLaunch = ticket; heldSessions.Add(sessionId);
            if (PlayniteApi.MainView.UIDispatcher.CheckAccess()) uiStartupCleanup[sessionId] = args.Game.Id;
            return ticket;
        }
        private void WaitForSelectedPlay(OnGameStartingEventArgs args, SelectedLaunchGate ticket, long request)
        {
            bool release = false;
            try { release = ticket.Wait(Monitor,PlayniteApi.MainView.UIDispatcher); }
            finally
            {
                lock (selectionGate)
                {
                    release &= !args.CancelStartup && IsCurrentLaunch(request);
                    if (!release) { args.CancelStartup = true; RecordStartupCancellation(args.Game.Id,ticket.SessionId); }
                    else uiStartupCleanup.Remove(ticket.SessionId);
                    if (selectedLaunch == ticket) selectedLaunch = null;
                    if (release || Monitor.Current.SessionId != ticket.SessionId) heldSessions.Remove(ticket.SessionId);
                }
            }
            Log(release ? "Selected update complete; releasing SDK Play" : "Discarded held SDK Play",false);
        }
        private bool IsCurrentLaunch(long request)
        { lock (selectionGate) return !closing && !stopped && Settings.Enabled && request == launchRequest; }
        private void CancelReplacementStartup(OnGameStartingEventArgs args, long request)
        {
            args.CancelStartup = true;
            lock (selectionGate) {
                // No monitor session was created for this startup. A negative
                // request token keeps its delayed SDK acknowledgement separate.
                RecordStartupCancellation(args.Game.Id,-request);
            }
        }
        // Call while holding selectionGate, before the SDK acknowledges cancellation.
        private void RecordStartupCancellation(Guid gameId, long token)
        {
            Queue<long> cancellations;
            if (!ownStartupCancellations.TryGetValue(gameId,out cancellations))
                ownStartupCancellations[gameId] = cancellations = new Queue<long>();
            cancellations.Enqueue(token);
        }
        public override void OnGameStarted(OnGameStartedEventArgs args) { if (closing || stopped) return; Log("Game started " + args.Game.Id, false); Monitor.Stop(args.Game.Id); }
        public override void OnGameStopped(OnGameStoppedEventArgs args) { if (!closing && !stopped) Monitor.Stop(args.Game.Id); }
        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            if (closing || stopped) return;
            Log("Launch cancelled " + args.Game.Id, false);
            long cancelledSession = 0; bool navigate = false, restart = false;
            lock (selectionGate) {
                Queue<long> cancellations;
                if (ownStartupCancellations.TryGetValue(args.Game.Id,out cancellations)) {
                    cancelledSession = cancellations.Dequeue();
                    uiStartupCleanup.Remove(cancelledSession);
                    if (cancellations.Count == 0) ownStartupCancellations.Remove(args.Game.Id);
                    if (pausedLaunch != null && pausedLaunch.SessionId == cancelledSession) { pausedLaunch.SdkCancelled = true; heldSessions.Remove(cancelledSession); restart = true; }
                    else if (closedHeldSessions.Remove(cancelledSession)) navigate = true;
                    else if (heldSessions.Contains(cancelledSession)) sdkCancelledSessions.Add(cancelledSession);
                }
            }
            if (cancelledSession != 0) { if (restart) RestartPausedLaunch(); if (navigate) ReturnToLibrary(cancelledSession); return; }
            Monitor.Stop(args.Game.Id);
        }
        private void ReturnToLibrary(long sessionId)
        {
            var dispatcher = PlayniteApi.MainView.UIDispatcher;
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() => {
                if (closing || stopped || returnedToLibrarySession != sessionId || Monitor.Current.Visible && Monitor.Current.SessionId != sessionId) return;
                try { LibraryNavigation.Return(PlayniteApi); }
                catch (Exception e) { Log("Library return failed: " + e.Message,false); }
            }));
        }
    }
}
