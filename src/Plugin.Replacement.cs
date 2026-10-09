using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Playnite.SDK.Events;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.UI;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        private sealed class DeferredPlay
        {
            public Guid GameId, PreviousGameId;
            public long Request, PreviousSession;
            public Task<bool> Removal;
            public readonly HashSet<Guid> CleanupGames = new HashSet<Guid>();
            public DateTime? CleanupDeadline;
        }
        private DeferredPlay deferredPlay;
        private readonly Dictionary<Guid,Queue<DeferredPlay>> replacementReplays = new Dictionary<Guid,Queue<DeferredPlay>>();

        // Called under selectionGate. Public SDK callbacks are synchronous on the
        // current host, but a delayed callback must still reject a superseded Play.
        private bool ConsumeReplacementReplay(OnGameStartingEventArgs args)
        {
            Queue<DeferredPlay> replays;
            if (!replacementReplays.TryGetValue(args.Game.Id,out replays)) return true;
            var replay = replays.Dequeue();
            if (replays.Count == 0) replacementReplays.Remove(args.Game.Id);
            var state = Monitor.Current;
            if (IsCurrentLaunch(replay.Request) && state.SessionId == replay.PreviousSession && !state.Dismissed) return true;
            CancelReplacementStartup(args,replay.Request);
            return false;
        }
        private void DeferReplacementPlay(OnGameStartingEventArgs args, long request, UpdateState old, SelectedLaunchGate previous)
        {
            var pending = new DeferredPlay { GameId = args.Game.Id, PreviousGameId = old.GameId, PreviousSession = old.SessionId, Request = request };
            lock (selectionGate)
            {
                if (!IsCurrentLaunch(request)) { CancelReplacementStartup(args,request); return; }
                foreach (var gameId in uiStartupCleanup.Values) pending.CleanupGames.Add(gameId);
                if (deferredPlay != null) pending.CleanupGames.UnionWith(deferredPlay.CleanupGames);
                pending.CleanupGames.Add(args.Game.Id);
                deferredPlay = pending;
                CancelReplacementStartup(args,request);
                uiStartupCleanup[-request] = args.Game.Id;
            }
            bool remove = old.Visible && old.GameId != args.Game.Id && Monitor.CanRemoveForReplacement &&
                (old.Phase == UpdatePhase.Updating || old.Phase == UpdatePhase.Error && observedUpdate || old.CommandPending || previous != null && previous.SessionId == old.SessionId);
            pending.Removal = remove ? Monitor.RemoveForReplacement(old.GameId,old.SessionId) : Task.FromResult(true);
            Log("Replacement Play waits for previous UI startup cleanup",false);
            var dispatcher = PlayniteApi.MainView.UIDispatcher;
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() =>
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
                timer.Tick += (s,e) => { if (TryDispatchReplacement(pending)) timer.Stop(); };
                if (!TryDispatchReplacement(pending)) timer.Start();
            }));
        }
        // True means this timer is finished, including cancellation or refusal.
        private bool TryDispatchReplacement(DeferredPlay pending)
        {
            var dispatcher = PlayniteApi.MainView.UIDispatcher;
            lock (selectionGate)
            {
                if (dispatcher.HasShutdownStarted || deferredPlay != pending || !IsCurrentLaunch(pending.Request)) return true;
                var state = Monitor.Current;
                if (state.SessionId != pending.PreviousSession || state.Dismissed) { deferredPlay = null; return true; }
                if (!pending.Removal.IsCompleted) return false;
                if (pending.Removal.Status != TaskStatus.RanToCompletion || !pending.Removal.Result)
                { deferredPlay = null; Log("Replacement Play cancelled: previous update removal was not confirmed",false); return true; }
                if (!pending.CleanupDeadline.HasValue) pending.CleanupDeadline = DateTime.UtcNow.AddSeconds(3);
                bool waiting = uiStartupCleanup.Count != 0;
                try
                {
                    foreach (var id in pending.CleanupGames)
                    {
                        var game = PlayniteApi.Database.Games.Get(id);
                        if (game == null || game.Id != id) throw new InvalidOperationException("Startup game unavailable");
                        if (id == pending.GameId && game.IsRunning) { deferredPlay = null; return true; }
                        waiting |= game.IsLaunching;
                    }
                }
                catch (Exception e) { ReplacementUnavailable(pending,e.Message); return true; }
                if (waiting)
                {
                    if (DateTime.UtcNow > pending.CleanupDeadline.Value) { ReplacementUnavailable(pending,"Host cancellation cleanup not confirmed"); return true; }
                    return false;
                }
                deferredPlay = null;
                Queue<DeferredPlay> replays;
                if (!replacementReplays.TryGetValue(pending.GameId,out replays)) replacementReplays[pending.GameId] = replays = new Queue<DeferredPlay>();
                replays.Enqueue(pending);
            }
            try { PlayniteApi.StartGame(pending.GameId); }
            catch (Exception e)
            {
                lock (selectionGate)
                {
                    Queue<DeferredPlay> replays;
                    if (replacementReplays.TryGetValue(pending.GameId,out replays) && replays.Count != 0 && replays.Peek() == pending)
                    { replays.Dequeue(); if (replays.Count == 0) replacementReplays.Remove(pending.GameId); }
                }
                ReplacementUnavailable(pending,e.Message);
            }
            return true;
        }
        private void ReplacementUnavailable(DeferredPlay pending, string reason)
        {
            if (deferredPlay == pending) deferredPlay = null;
            Monitor.ReportNotice(pending.PreviousGameId,pending.PreviousSession,"Unable to restart — press Play again");
            Log("Replacement SDK startup unavailable: " + reason,false);
        }
    }
}
