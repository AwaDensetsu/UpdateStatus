using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteUpdateStatus.Core
{
    public enum ExitDownloadAction { KeepCurrentState, PauseAndKeepInQueue, PauseAndRemoveFromQueue }

    public sealed partial class UpdateMonitor
    {
        private bool exiting;
        private int commandsExecuting;
        private Task<bool> exitCompletion;

        public Task<bool> PrepareForExit(ExitDownloadAction action, TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("budget");
            lock (gate)
            {
                if (exitCompletion != null) return exitCompletion;
                exiting = true;
                long token = ++generation;
                if (timer != null) { timer.Dispose(); timer = null; }
                var active = session as IUpdateControlSession;
                bool eligible = ControlsEnabled && active != null && current.Visible && !current.Dismissed &&
                    (current.Phase == UpdatePhase.Updating || current.Phase == UpdatePhase.Error ||
                        current.Phase == UpdatePhase.Launching && active is IUpdateExitSession);
                try { var preparation = session as IUpdateExitSession; if (preparation != null) preparation.PrepareForExit(); }
                catch (Exception e) { log("Exit preparation unavailable: " + e.Message,true); return exitCompletion = Task.FromResult(false); }
                if (!Enum.IsDefined(typeof(ExitDownloadAction),action)) return exitCompletion = Task.FromResult(false);
                if (action == ExitDownloadAction.KeepCurrentState || !eligible) return exitCompletion = Task.FromResult(true);
                if (action == ExitDownloadAction.PauseAndRemoveFromQueue && !(active is IUpdateCloseSession)) return exitCompletion = Task.FromResult(false);
                var clock = Stopwatch.StartNew();
                return exitCompletion = Task.Run(() => ApplyExitAction(active,token,action,clock,budget));
            }
        }
        private bool ApplyExitAction(IUpdateControlSession active, long token, ExitDownloadAction action, Stopwatch clock, TimeSpan budget)
        {
            Func<bool> valid = () => { lock (gate) return generation == token && session == active && clock.Elapsed < budget; };
            try
            {
                // Drain operations which acquired the session before invalidation;
                // a committed Resume/selection must not undo the final exit action.
                while (Interlocked.CompareExchange(ref polling,0,0) != 0 || Interlocked.CompareExchange(ref commandsExecuting,0,0) != 0)
                { if (!valid()) return false; Thread.Sleep(15); }
                if (!valid()) return false;
                var before = active.Poll();
                if (!valid()) return false;
                if (before.Phase == UpdatePhase.Ready || before.Phase == UpdatePhase.Running || before.Phase == UpdatePhase.Idle) return true;
                bool remove = action == ExitDownloadAction.PauseAndRemoveFromQueue;
                if (remove && before.CloseConfirmed == true) return true;
                if (!remove && before.ControlPaused == true) return true;
                if (!before.ControlPaused.HasValue) return false;
                if (!(remove ? ((IUpdateCloseSession)active).SendClose() : active.SendCommand(UpdateCommand.Pause))) return false;
                while (valid())
                {
                    var observed = active.Poll();
                    if (!valid()) return false;
                    if (remove ? observed.CloseConfirmed == true : observed.ControlPaused == true) return true;
                    Thread.Sleep(50);
                }
            }
            catch (Exception e) { log("Exit update action unavailable: " + e.Message,true); }
            return false;
        }
    }
}
