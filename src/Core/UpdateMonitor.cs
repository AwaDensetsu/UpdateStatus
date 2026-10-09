using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteUpdateStatus.Core
{
    public sealed partial class UpdateMonitor : IDisposable
    {
        private readonly object gate = new object();
        private readonly Action<string, bool> log;
        private Timer timer;
        private IUpdateSession session;
        private LaunchIdentity game;
        private long generation, revision;
        private int polling;
        private DateTime launchingSince;
        private UpdateCommand? requestedCommand;
        private bool commandSent;
        private bool dismissAfterPause;
        private DateTime commandDeadline;
        private string commandNotice;
        private long commandSequence;
        private long dismissedGeneration = -1;
        private TaskCompletionSource<bool> replacementRemoval;
        private UpdateState current = new UpdateState();
        public event Action<UpdateState> Changed;
        public event Action<Guid, long, UpdateCommand> CommandConfirmed;
        public event Action<Guid, long, UpdateCommand> CommandRequested;
        public event Action<Guid, long> CloseRequested;
        public bool ControlsEnabled { get; set; }
        public SpeedUnit SpeedUnit { get; set; }
        public UpdateState Current { get { lock (gate) return current; } }
        public bool CanRemoveForReplacement { get { lock (gate) return current.Visible && session is IUpdateCloseSession; } }
        private bool ReplacementPending { get { return replacementRemoval != null && !replacementRemoval.Task.IsCompleted; } }

        public UpdateMonitor(Action<string, bool> log) { this.log = log; ControlsEnabled = true; }

        public UpdateState Start(LaunchIdentity identity, IUpdateSession updateSession, int interval)
        {
            lock (gate) { if (exiting) throw new ObjectDisposedException("UpdateMonitor"); }
            Stop(Guid.Empty,null,true);
            lock (gate)
            {
                if (exiting) { updateSession.Dispose(); throw new ObjectDisposedException("UpdateMonitor"); }
                game = identity;
                session = updateSession;
                launchingSince = DateTime.UtcNow;
                var token = ++generation;
                dismissedGeneration = -1;
                current = new UpdateState { SessionId = token, GameId = identity.GameId, GameName = identity.Name, LauncherName = identity.LauncherName, Phase = UpdatePhase.Launching, Revision = ++revision };
                timer = new Timer(_ => Tick(token), null, 0, Math.Max(500, Math.Min(2000, interval)));
                return current;
            }
        }

        private void Tick(long token)
        {
            if (Interlocked.Exchange(ref polling, 1) != 0) return;
            try
            {
                IUpdateSession active;
                LaunchIdentity identity;
                lock (gate)
                {
                    if (token != generation || session == null) return;
                    active = session; identity = game;
                }
                UpdateObservation observation;
                try { observation = active.Poll(); }
                catch (Exception e)
                {
                    observation = new UpdateObservation { Phase = UpdatePhase.Error, Detail = e.Message };
                }
                UpdateState next;
                UpdateCommand? confirmedCommand = null;
                lock (gate)
                {
                    if (token != generation) return;
                    // Timeout only an unconfirmed launch, never a long/paused update.
                    if (observation.Phase == UpdatePhase.Launching)
                    {
                        if (current.Phase != UpdatePhase.Launching) launchingSince = DateTime.UtcNow;
                        if ((DateTime.UtcNow - launchingSince).TotalMinutes > 3)
                            observation = new UpdateObservation { Phase = UpdatePhase.Idle, Detail = "Launch observation timed out" };
                    }
                    if (requestedCommand.HasValue)
                    {
                        bool closing = requestedCommand == UpdateCommand.Close;
                        bool confirmed = commandSent && closing && observation.CloseConfirmed == true;
                        confirmed |= commandSent && !closing && (observation.CanPause || observation.CanResume) && observation.Display != null &&
                            !observation.Display.WaitingForData && (observation.ControlPaused ?? observation.Display.Paused) == (requestedCommand == UpdateCommand.Pause);
                        // The update can finish between acknowledgement and the next read.
                        // Fresh native global state still confirms the explicit command.
                        confirmed |= commandSent && !closing && observation.ControlPaused.HasValue &&
                            (observation.Phase == UpdatePhase.Ready || observation.Phase == UpdatePhase.Launching) &&
                            observation.ControlPaused.Value == (requestedCommand == UpdateCommand.Pause);
                        // Completion may be observed while SendCommand is still returning
                        // its acknowledgement. Keep that request through Ready/Launching
                        // so fresh global state can confirm it on the following tick.
                        if (confirmed || observation.Phase == UpdatePhase.Idle || observation.Phase == UpdatePhase.Running || !closing && observation.Phase == UpdatePhase.Error)
                        {
                            if (confirmed) confirmedCommand = requestedCommand;
                            if (closing && replacementRemoval != null) replacementRemoval.TrySetResult(confirmed);
                            if (confirmed && dismissAfterPause) dismissedGeneration = token;
                            requestedCommand = null; commandNotice = null; dismissAfterPause = false;
                        }
                        else if (DateTime.UtcNow > commandDeadline) { requestedCommand = null; dismissAfterPause = false; commandNotice = "Command not confirmed — check the launcher"; if (closing && replacementRemoval != null) replacementRemoval.TrySetResult(false); }
                    }
                    next = new UpdateState { GameId = identity.GameId, GameName = identity.Name, LauncherName = identity.LauncherName, Phase = observation.Phase, Percent = observation.Percent,
                        SessionId = token, Dismissed = dismissedGeneration == token,
                        CanPause = observation.CanPause, CanResume = observation.CanResume, CommandPending = requestedCommand.HasValue || ReplacementPending, CommandNotice = commandNotice,
                        ControlPaused = observation.ControlPaused, ControlScope = observation.ControlScope,
                        Detail = observation.Detail, Display = observation.Display, Revision = ++revision };
                    if (next.Phase != current.Phase) log("Phase " + identity.LauncherId + " " + current.Phase + " -> " + next.Phase +
                        (next.Phase == UpdatePhase.Error ? ": " + next.Detail : ""), next.Phase == UpdatePhase.Error);
                    current = next;
                }
                var handler = Changed;
                var confirmedHandler = CommandConfirmed;
                if (confirmedCommand.HasValue && confirmedHandler != null) confirmedHandler(identity.GameId, token, confirmedCommand.Value);
                if (handler != null) handler(next);
                if (next.Phase == UpdatePhase.Idle || next.Phase == UpdatePhase.Running) Stop(identity.GameId, token);
            }
            finally { Interlocked.Exchange(ref polling, 0); }
        }

        public void Stop(Guid gameId) { Stop(gameId, null); }
        public void ReportNotice(Guid gameId, long sessionId, string notice)
        {
            UpdateState next;
            lock (gate) { if (game == null || game.GameId != gameId || generation != sessionId) return; commandNotice = notice; next = CopyControlState(); }
            var changed = Changed; if (changed != null) changed(next);
        }
        public bool SetStatusDismissed(Guid gameId, long sessionId, bool dismissed)
        {
            UpdateState next;
            lock (gate)
            {
                if (game == null || game.GameId != gameId || generation != sessionId || !current.Visible) return false;
                dismissedGeneration = dismissed ? generation : -1;
                next = CopyControlState();
            }
            var changed = Changed; if (changed != null) changed(next);
            return true;
        }
        public bool RequestClose(Guid gameId, long sessionId)
        {
            UpdateState next = null;
            bool dismissed = false;
            lock (gate)
            {
                if (exiting || game == null || game.GameId != gameId || generation != sessionId || !current.Visible || current.Dismissed || requestedCommand.HasValue || ReplacementPending) return false;
                if (session is IUpdateCloseSession)
                {
                    if (!ControlsEnabled) { commandNotice = "Close unavailable — check the launcher"; next = CopyControlState(); }
                }
                else if (current.Phase != UpdatePhase.Updating || current.CanResume && current.ControlPaused != false)
                {
                    dismissedGeneration = generation; dismissed = true; next = CopyControlState();
                }
                else if (!ControlsEnabled || !current.CanPause || !(session is IUpdateControlSession))
                {
                    commandNotice = "Pause unavailable — check the launcher"; next = CopyControlState();
                }
            }
            var closing = CloseRequested; if (closing != null) closing(gameId,sessionId);
            if (next != null) { var changed = Changed; if (changed != null) changed(next); return dismissed; }
            bool queueClose; lock (gate) { queueClose = session is IUpdateCloseSession; }
            return RequestCommand(gameId,sessionId,queueClose ? UpdateCommand.Close : UpdateCommand.Pause,true);
        }
        public bool RequestCommand(Guid gameId, UpdateCommand command) { return RequestCommand(gameId,null,command,false); }
        // A handoff reuses close's pause/removal protocol, without dismissing the
        // old card or navigating away from the newly requested game. Concurrent
        // replacement requests share one removal and one fresh confirmation.
        public Task<bool> RemoveForReplacement(Guid gameId, long sessionId)
        {
            TaskCompletionSource<bool> completion;
            UpdateState pending;
            lock (gate)
            {
                if (exiting || game == null || game.GameId != gameId || generation != sessionId || !(session is IUpdateCloseSession) || !ControlsEnabled) return Task.FromResult(false);
                if (current.Dismissed) return Task.FromResult(true);
                if (replacementRemoval != null && (!replacementRemoval.Task.IsCompleted || replacementRemoval.Task.Result)) return replacementRemoval.Task;
                completion = replacementRemoval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (requestedCommand == UpdateCommand.Close) { dismissAfterPause = false; return completion.Task; }
                commandNotice = requestedCommand.HasValue ? "Waiting for current download command…" : "Removing previous update from queue…";
                pending = CopyControlState();
            }
            var changed = Changed; if (changed != null) changed(pending);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // An already accepted Pause/Resume must settle before removal;
                // it must never pause/resume the next game's download later.
                var deadline = DateTime.UtcNow.AddSeconds(16);
                while (true)
                {
                    lock (gate)
                    {
                        if (generation != sessionId || session == null || completion != replacementRemoval) { completion.TrySetResult(false); return; }
                        if (!requestedCommand.HasValue) break;
                        if (DateTime.UtcNow > deadline) { completion.TrySetResult(false); return; }
                    }
                    Thread.Sleep(25);
                }
                if (!RequestCommand(gameId,sessionId,UpdateCommand.Close,false,true)) completion.TrySetResult(false);
            });
            return completion.Task;
        }
        private bool RequestCommand(Guid gameId, long? sessionId, UpdateCommand command, bool closeAfterPause, bool replacement = false)
        {
            IUpdateControlSession active;
            long token;
            long requestToken;
            UpdateState pending;
            lock (gate)
            {
                active = session as IUpdateControlSession;
                bool queueClose = command == UpdateCommand.Close && (closeAfterPause || replacement) && active is IUpdateCloseSession && current.Visible && !current.Dismissed;
                if (exiting || !ControlsEnabled || active == null || game == null || game.GameId != gameId || sessionId.HasValue && generation != sessionId.Value || !queueClose && current.Phase != UpdatePhase.Updating || requestedCommand.HasValue ||
                    !replacement && ReplacementPending || command != UpdateCommand.Pause && command != UpdateCommand.Resume && !queueClose || command == UpdateCommand.Pause && !current.CanPause || command == UpdateCommand.Resume && !current.CanResume) return false;
                token = generation; requestedCommand = command; commandSent = false;
                dismissAfterPause = closeAfterPause;
                requestToken = ++commandSequence;
                commandDeadline = DateTime.UtcNow.AddSeconds(15);
                commandNotice = command == UpdateCommand.Close ? "Removing update from queue…" : command == UpdateCommand.Pause ? "Requesting pause…" : "Requesting resume…";
                pending = CopyControlState();
            }
            var requested = CommandRequested; if (requested != null) requested(gameId,token,command);
            var handler = Changed; if (handler != null) handler(pending);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (gate) { if (token != generation || requestToken != commandSequence || session != active || requestedCommand != command) return; commandsExecuting++; }
                bool accepted = false;
                try { accepted = command == UpdateCommand.Close ? ((IUpdateCloseSession)active).SendClose() : active.SendCommand(command); }
                catch (Exception e) { log("Update command failed: " + e.Message,false); }
                finally { Interlocked.Decrement(ref commandsExecuting); }
                UpdateState next;
                lock (gate)
                {
                    if (token != generation || requestToken != commandSequence || session != active || requestedCommand != command) return;
                    commandSent = accepted;
                    if (!accepted) { requestedCommand = null; dismissAfterPause = false; commandNotice = "Command not accepted — check the launcher"; if (command == UpdateCommand.Close && replacementRemoval != null) replacementRemoval.TrySetResult(false); }
                    else commandNotice = "Waiting for launcher confirmation…";
                    next = CopyControlState();
                }
                var changed = Changed; if (changed != null) changed(next);
            });
            return true;
        }
        private UpdateState CopyControlState()
        {
            return current = new UpdateState { Revision = ++revision, GameId = current.GameId, GameName = current.GameName, LauncherName = current.LauncherName,
                SessionId = current.SessionId, Dismissed = dismissedGeneration == current.SessionId,
                Phase = current.Phase, Percent = current.Percent, Display = current.Display, Detail = current.Detail,
                ControlPaused = current.ControlPaused, ControlScope = current.ControlScope,
                CanPause = current.CanPause, CanResume = current.CanResume, CommandPending = requestedCommand.HasValue || ReplacementPending, CommandNotice = commandNotice };
        }
        private void Stop(Guid gameId, long? token, bool starting = false)
        {
            UpdateState idle;
            lock (gate)
            {
                if (starting && exiting) throw new ObjectDisposedException("UpdateMonitor");
                if (token.HasValue && token.Value != generation) return;
                if (gameId != Guid.Empty && (game == null || game.GameId != gameId)) return;
                ++generation;
                if (replacementRemoval != null) { replacementRemoval.TrySetResult(false); replacementRemoval = null; }
                dismissedGeneration = -1;
                requestedCommand = null; commandSent = false; dismissAfterPause = false; commandNotice = null;
                if (timer != null) { timer.Dispose(); timer = null; }
                // Session disposal cancels its own helper. Generation checks discard any
                // already-running read; adapters must not recreate resources after disposal.
                if (session != null) { session.Dispose(); session = null; }
                game = null;
                idle = current = new UpdateState { Revision = ++revision, Phase = UpdatePhase.Idle };
            }
            var handler = Changed;
            if (handler != null) handler(idle);
        }
        public void Dispose() { Stop(Guid.Empty); }
    }
}
