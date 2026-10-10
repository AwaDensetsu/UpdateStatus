using System;
using System.Threading;
using NUnit.Framework;
using PlayniteUpdateStatus.Core;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class CloseRequestGuardTests
    {
        private sealed class Session : IUpdateCloseSession
        {
            public int CloseCalls;
            public UpdateObservation Poll() { return new UpdateObservation { Phase = UpdatePhase.Updating, CanPause = true, ControlPaused = false }; }
            public bool SendCommand(UpdateCommand command) { return false; }
            public bool SendClose() { CloseCalls++; return true; }
            public void Dispose() { }
        }

        [Test]
        public void DisabledControlsRejectCloseWithoutCancellingPlayOrSendingACommand()
        {
            using (var observed = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((s,w) => { }))
            {
                var session = new Session();
                var gameId = Guid.NewGuid();
                int cancellations = 0;
                monitor.Changed += state => { if (state.CanPause) observed.Set(); };
                monitor.CloseRequested += (id, token) => Interlocked.Increment(ref cancellations);
                var start = monitor.Start(new LaunchIdentity { GameId = gameId }, session, 500);
                Assert.That(observed.WaitOne(2000), Is.True);
                monitor.ControlsEnabled = false;
                Assert.That(monitor.RequestClose(gameId, start.SessionId), Is.False);
                Assert.That(cancellations, Is.Zero);
                Assert.That(session.CloseCalls, Is.Zero);
                Assert.That(monitor.Current.Dismissed, Is.False);
                Assert.That(monitor.Current.CommandPending, Is.False);
            }
        }
    }
}
