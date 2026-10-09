using System;
using System.Threading;
using System.Windows.Threading;
using Playnite.SDK.Events;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.UI
{
    // Retains one SDK callback/action until release or cancellation. Pause and
    // replacement mark its args immediately; confirmed Resume is handled by the
    // plugin through a separate public SDK startup after host cancellation.
    public sealed class SelectedLaunchGate
    {
        private readonly OnGameStartingEventArgs args;
        private volatile bool cancelled;
        private volatile bool discarded;
        public Guid GameId { get { return args.Game.Id; } }
        public long SessionId { get; private set; }
        public SelectedLaunchGate(OnGameStartingEventArgs args, long session) { this.args = args; SessionId = session; }
        public void Cancel() { cancelled = true; args.CancelStartup = true; }
        public void DiscardPlay() { discarded = true; args.CancelStartup = true; }
        public bool Wait(UpdateMonitor monitor, Dispatcher dispatcher)
        {
            using (var wake = new ManualResetEvent(false))
            {
                Action<UpdateState> changed = state => { try { wake.Set(); } catch (ObjectDisposedException) { } };
                monitor.Changed += changed;
                Func<bool> finished = () => {
                    var state = monitor.Current;
                    if (cancelled || dispatcher.HasShutdownStarted || state.GameId != GameId || state.SessionId != SessionId || state.Dismissed ||
                        state.Phase == UpdatePhase.Idle || state.Phase == UpdatePhase.Running) { Cancel(); return true; }
                    // Errors remain visible/cancellable. A saved error or a transient
                    // Launching observation can never release or silently cancel Play.
                    return !discarded && state.Phase == UpdatePhase.Ready && !state.CommandPending && state.ControlPaused == false;
                };
                try
                {
                    if (dispatcher.CheckAccess())
                    {
                        var frame = new DispatcherFrame();
                        var timer = new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
                        timer.Tick += (s,e) => { if (finished()) frame.Continue = false; };
                        try { if (!finished()) { timer.Start(); Dispatcher.PushFrame(frame); } }
                        finally { timer.Stop(); }
                        if (dispatcher.HasShutdownStarted) Cancel();
                    }
                    else while (!finished()) { wake.WaitOne(50); wake.Reset(); }
                    return !cancelled;
                }
                finally { monitor.Changed -= changed; }
            }
        }
    }
}
