using System;
using System.Windows.Threading;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        private sealed class PausedLaunch
        {
            public Guid GameId;
            public long SessionId, Request;
            public bool SdkCancelled, ResumeConfirmed, RestartScheduled;
        }
        private PausedLaunch pausedLaunch, queuedResume;

        private void CommandRequested(Guid id, long token, UpdateCommand command)
        {
            if (closing || stopped) return;
            var recovery = steamRecovery; if (recovery != null && token == recoverySession) recovery.CommandRequested(command);
            if (command != UpdateCommand.Pause) return;
            lock (selectionGate)
            {
                if (pausedLaunch != null && pausedLaunch.GameId == id && pausedLaunch.SessionId == token) { pausedLaunch.ResumeConfirmed = false; return; }
                var ticket = selectedLaunch;
                if (ticket == null || ticket.GameId != id || ticket.SessionId != token) return;
                pausedLaunch = new PausedLaunch { GameId = id, SessionId = token, Request = launchRequest };
                ticket.Cancel();
                Log("Pause cancels the held SDK startup; update observation remains available",false);
            }
        }
        private void CommandConfirmed(Guid id, long token, UpdateCommand command)
        {
            if (closing || stopped) return;
            var recovery = steamRecovery; if (recovery != null && token == recoverySession) recovery.CommandConfirmed(command);
            if (command != UpdateCommand.Resume) return;
            lock (selectionGate)
            { if (pausedLaunch == null || pausedLaunch.GameId != id || pausedLaunch.SessionId != token) return; pausedLaunch.ResumeConfirmed = true; }
            Log("Resume restores selected SDK Play after cancellation cleanup",false);
            RestartPausedLaunch();
        }
        private void RestartPausedLaunch()
        {
            PausedLaunch candidate;
            var dispatcher = PlayniteApi.MainView.UIDispatcher;
            lock (selectionGate)
            {
                candidate = pausedLaunch;
                if (candidate == null || !candidate.SdkCancelled || !candidate.ResumeConfirmed || candidate.RestartScheduled || !IsCurrentLaunch(candidate.Request)) return;
                var state = Monitor.Current;
                if (state.GameId != candidate.GameId || state.SessionId != candidate.SessionId || !CanRetainResumedPlay(state)) return;
                candidate.RestartScheduled = true;
            }
            if (dispatcher.HasShutdownStarted) return;
            DispatchPausedRestart(candidate,DateTime.UtcNow.AddSeconds(3));
        }
        private void DispatchPausedRestart(PausedLaunch candidate, DateTime deadline)
        {
            var dispatcher = PlayniteApi.MainView.UIDispatcher;
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() =>
            {
                lock (selectionGate)
                {
                    var state = Monitor.Current;
                    if (pausedLaunch != candidate || !candidate.ResumeConfirmed || !IsCurrentLaunch(candidate.Request) || state.SessionId != candidate.SessionId || !CanRetainResumedPlay(state))
                    { if (pausedLaunch == candidate) candidate.RestartScheduled = false; return; }
                    if (activeGame == null || activeGame.Id != candidate.GameId) { candidate.RestartScheduled = false; return; }
                    // Starting/cancelled SDK events contain snapshots. Their flags
                    // do not change when the host clears the actual database game.
                    Playnite.SDK.Models.Game liveGame;
                    try { liveGame = PlayniteApi.Database.Games.Get(candidate.GameId); }
                    catch (Exception e) { candidate.RestartScheduled = false; Monitor.ReportNotice(candidate.GameId,candidate.SessionId,"Unable to restart — press Play again"); Log("Resume game lookup unavailable: " + e.Message,false); return; }
                    if (liveGame == null || liveGame.Id != candidate.GameId) { candidate.RestartScheduled = false; Monitor.ReportNotice(candidate.GameId,candidate.SessionId,"Unable to restart — press Play again"); Log("Resume game no longer available in Playnite database",false); return; }
                    if (liveGame.IsRunning) { candidate.ResumeConfirmed = false; candidate.RestartScheduled = false; return; }
                    // Some hosts clear IsLaunching after their cancellation event.
                    // Wait for current database state; never edit execution flags.
                    if (liveGame.IsLaunching)
                    {
                        if (DateTime.UtcNow > deadline) { candidate.RestartScheduled = false; Monitor.ReportNotice(candidate.GameId,candidate.SessionId,"Unable to restart — press Play again"); Log("Resume startup still tracked by Playnite; press Play again after cleanup",false); return; }
                        var timer = new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
                        timer.Tick += (s,e) => { timer.Stop(); DispatchPausedRestart(candidate,deadline); }; timer.Start(); return;
                    }
                    // The public SDK owns the new startup/controller/scripts. A
                    // late callback is checked again before replacing observation.
                    queuedResume = candidate;
                }
                try { PlayniteApi.StartGame(candidate.GameId); Log("Resume requested a new SDK startup for the paused selection",false); }
                catch (Exception e) { lock (selectionGate) { candidate.ResumeConfirmed = false; candidate.RestartScheduled = false; if (queuedResume == candidate) queuedResume = null; } Monitor.ReportNotice(candidate.GameId,candidate.SessionId,"Unable to restart — press Play again"); Log("Resume startup unavailable; press Play again: " + e.Message,false); }
            }));
        }
        private static bool CanRetainResumedPlay(UpdateState state)
        { return (state.Phase == UpdatePhase.Updating || state.Phase == UpdatePhase.Ready) && !state.Dismissed && !state.CommandPending && state.ControlPaused == false; }
    }
}
