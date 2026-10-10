using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NUnit.Framework;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("SelectedSession")]
    public sealed class SelectedLaunchGateFailureTests
    {
        private sealed class Session : IUpdateControlSession
        {
            public volatile UpdatePhase Phase = UpdatePhase.Updating;
            public UpdateObservation Poll() { return new UpdateObservation { Phase = Phase, ControlPaused = false }; }
            public bool SendCommand(UpdateCommand command) { return false; }
            public void Dispose() { }
        }

        private static OnGameStartingEventArgs Starting(Game game)
        {
            var args = new OnGameStartingEventArgs();
            typeof(OnGameStartingEventArgs).GetProperty("Game").SetValue(args, game, null);
            return args;
        }

        [Test]
        public void TerminalConnectionFailureReturnsSdkCallbackWithStartupCancelled()
        {
            bool failed = false;
            var game = new Game { Id = Guid.NewGuid(), Name = "Synthetic update" };
            var args = Starting(game);
            using (var errorSeen = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((s,w) => { }))
            {
                monitor.Changed += snapshot => { if (snapshot.Phase == UpdatePhase.Error) errorSeen.Set(); };
                var session = new SteamSelectedUpdateSession(550, new Session(), () => {
                    if (Volatile.Read(ref failed)) throw new InvalidOperationException("Steam exited or restarted");
                    return SelectedSessionFailureTests.Frame(DateTime.UtcNow);
                }, () => SteamSelectionResult.Accepted, () => { }, () => { }, (s,w) => { });
                var state = monitor.Start(new LaunchIdentity { GameId = game.Id, LauncherId = "550" }, session, 500);
                var ticket = new SelectedLaunchGate(args, state.SessionId);
                var dispatcher = Dispatcher.CurrentDispatcher;
                var wait = Task.Run(() => ticket.Wait(monitor, dispatcher));
                try
                {
                    Volatile.Write(ref failed, true);
                    Assert.That(errorSeen.WaitOne(2000), Is.True);
                    Assert.That(monitor.Current.CancelPendingPlay, Is.True);
                    monitor.ReportNotice(game.Id, state.SessionId, "Synthetic notice");
                    Assert.That(monitor.Current.CancelPendingPlay, Is.True);
                    Assert.That(wait.Wait(1500), Is.True, "The SDK callback must finish without X or a replacement Play");
                    Assert.That(wait.Result, Is.False);
                    Assert.That(args.CancelStartup, Is.True);
                }
                finally { if (!wait.IsCompleted) ticket.Cancel(); Assert.That(wait.Wait(1500), Is.True); }
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void TerminalFailureUnwindsTheUiDispatcherFrame()
        {
            bool fallbackUsed = false;
            var game = new Game { Id = Guid.NewGuid(), Name = "Synthetic update" };
            var args = Starting(game);
            using (var monitor = new UpdateMonitor((s,w) => { }))
            {
                var session = new SteamSelectedUpdateSession(550, new Session(),
                    () => { throw new InvalidOperationException("Steam exited or restarted"); },
                    () => SteamSelectionResult.Accepted, () => { }, () => { }, (s,w) => { });
                var state = monitor.Start(new LaunchIdentity { GameId = game.Id, LauncherId = "550" }, session, 500);
                var ticket = new SelectedLaunchGate(args, state.SessionId);
                var dispatcher = Dispatcher.CurrentDispatcher;
                var fallback = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                { Interval = TimeSpan.FromSeconds(2) };
                fallback.Tick += (s,e) => { fallbackUsed = true; ticket.Cancel(); fallback.Stop(); };
                try
                {
                    fallback.Start();
                    Assert.That(ticket.Wait(monitor, dispatcher), Is.False);
                    Assert.That(args.CancelStartup, Is.True);
                    Assert.That(fallbackUsed, Is.False, "The terminal error must unwind the frame without a manual cancellation");
                }
                finally { fallback.Stop(); }
            }
        }
        [Test]
        public void ContinuousErrorsAfterFailedCloseReturnSdkCallbackAtDeadline()
        {
            long nowTicks = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc).Ticks;
            var game = new Game { Id = Guid.NewGuid(), Name = "Synthetic update" };
            var args = Starting(game);
            using (var errorSeen = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((s,w) => { }))
            {
                monitor.Changed += snapshot => { if (snapshot.Phase == UpdatePhase.Error) errorSeen.Set(); };
                var session = new SteamSelectedUpdateSession(550, new Session { Phase = UpdatePhase.Error },
                    () => { Assert.Fail("Cancelled Play must not read the original launch path"); return null; },
                    () => { Assert.Fail("Cancelled Play must not reselect the game"); return SteamSelectionResult.Rejected; },
                    () => { }, () => { }, (s,w) => { }, close: () => false,
                    utcNow: () => new DateTime(Interlocked.Read(ref nowTicks), DateTimeKind.Utc));
                Assert.That(session.SendClose(), Is.False);
                var state = monitor.Start(new LaunchIdentity { GameId = game.Id, LauncherId = "550" }, session, 500);
                var ticket = new SelectedLaunchGate(args, state.SessionId);
                var dispatcher = Dispatcher.CurrentDispatcher;
                var wait = Task.Run(() => ticket.Wait(monitor, dispatcher));
                try
                {
                    Assert.That(errorSeen.WaitOne(2000), Is.True);
                    Assert.That(wait.Wait(100), Is.False);
                    Interlocked.Add(ref nowTicks, TimeSpan.FromMinutes(1).Ticks);
                    Assert.That(wait.Wait(1500), Is.True, "Continuous observer errors must not retain the cancelled SDK callback indefinitely");
                    Assert.That(wait.Result, Is.False);
                    Assert.That(args.CancelStartup, Is.True);
                    Assert.That(monitor.Current.CancelPendingPlay, Is.True);
                }
                finally { if (!wait.IsCompleted) ticket.Cancel(); Assert.That(wait.Wait(1500), Is.True); }
            }
        }

        [Test]
        public void RecoverableErrorKeepsWaitUntilFreshReadyState()
        {
            var game = new Game { Id = Guid.NewGuid(), Name = "Synthetic update" };
            var args = Starting(game);
            using (var errorSeen = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((s,w) => { }))
            {
                var session = new Session { Phase = UpdatePhase.Error };
                monitor.Changed += snapshot => { if (snapshot.Phase == UpdatePhase.Error) errorSeen.Set(); };
                var state = monitor.Start(new LaunchIdentity { GameId = game.Id, LauncherId = "550" }, session, 500);
                var ticket = new SelectedLaunchGate(args, state.SessionId);
                var dispatcher = Dispatcher.CurrentDispatcher;
                var wait = Task.Run(() => ticket.Wait(monitor, dispatcher));
                try
                {
                    Assert.That(errorSeen.WaitOne(2000), Is.True);
                    Assert.That(monitor.Current.CancelPendingPlay, Is.False);
                    Assert.That(wait.Wait(100), Is.False);
                    Assert.That(args.CancelStartup, Is.False);
                    session.Phase = UpdatePhase.Ready;
                    Assert.That(wait.Wait(1500), Is.True);
                    Assert.That(wait.Result, Is.True);
                    Assert.That(args.CancelStartup, Is.False);
                }
                finally { if (!wait.IsCompleted) ticket.Cancel(); Assert.That(wait.Wait(1500), Is.True); }
            }
        }
    }
}
