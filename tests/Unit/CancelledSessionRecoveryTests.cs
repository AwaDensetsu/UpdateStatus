using System;
using System.IO;
using NUnit.Framework;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("SelectedSession")]
    public sealed class CancelledSessionRecoveryTests
    {
        private sealed class Observer : IUpdateControlSession
        {
            private readonly Func<UpdateObservation> poll;
            public Observer(Func<UpdateObservation> poll) { this.poll = poll; }
            public UpdateObservation Poll() { return poll(); }
            public bool SendCommand(UpdateCommand command) { return false; }
            public void Dispose() { }
        }

        private static UpdateObservation Missing(bool lastPaused = false)
        {
            return new UpdateObservation { Phase = UpdatePhase.Updating,
                Display = new UpdateDisplay { Stage = lastPaused ? "Paused" : "Downloading",
                    Paused = lastPaused, WaitingForData = true } };
        }

        private static SteamSelectedUpdateSession Open(Func<UpdateObservation> poll, Func<DateTime> clock,
            Func<bool> close, Func<bool?> confirmed = null)
        {
            return new SteamSelectedUpdateSession(550, new Observer(poll),
                () => { Assert.Fail("Cancelled selection must not read through its launch path"); return null; },
                () => { Assert.Fail("Cancelled selection must not reselect its update"); return SteamSelectionResult.Rejected; },
                () => Assert.Fail("Cancelled selection must not bootstrap Steam"), () => { }, (s,w) => { },
                close: close, closeConfirmed: confirmed, utcNow: clock);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedCloseTimesOutLostDataWithoutInventingPauseOrRevivingPlay(bool throws)
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            bool missing = true;
            using (var session = Open(() => missing ? Missing(true) : new UpdateObservation {
                Phase = UpdatePhase.Ready, ReceivedAtUtc = now }, () => now,
                () => { if (throws) throw new IOException("injected refusal"); return false; }))
            {
                if (throws) Assert.Throws<IOException>(() => session.SendClose());
                else Assert.That(session.SendClose(), Is.False);
                var waiting = session.Poll();
                Assert.That(waiting.Display.Stage, Is.EqualTo("Update data unavailable"));
                Assert.That(waiting.Display.Paused, Is.False);
                Assert.That(waiting.CanPause || waiting.CanResume, Is.False);
                now = now.AddMinutes(1);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                missing = false;
                var recovered = session.Poll();
                Assert.That(recovered.Phase, Is.EqualTo(UpdatePhase.Updating));
                Assert.That(recovered.Display.Stage, Is.EqualTo("Update complete"));
            }
        }

        [Test]
        public void UnconfirmedCloseTimesOutButFreshQueueConfirmationCanStillDismiss()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            bool confirmed = false;
            using (var session = Open(() => Missing(), () => now, () => true, () => confirmed))
            {
                Assert.That(session.SendClose(), Is.True);
                session.Poll();
                now = now.AddMinutes(1);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                confirmed = true;
                var reply = session.Poll();
                Assert.That(reply.CloseConfirmed, Is.True);
                Assert.That(reply.Phase, Is.EqualTo(UpdatePhase.Updating));
            }
        }

        [Test]
        public void FreshPausedMetadataWithoutCountersDoesNotExpireAfterFailedClose()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            using (var session = Open(() => new UpdateObservation { Phase = UpdatePhase.Updating,
                ReceivedAtUtc = now, ControlPaused = true,
                Display = new UpdateDisplay { Stage = "Paused", Paused = true, WaitingForData = true } },
                () => now, () => false))
            {
                session.SendClose();
                for (int step = 0; step < 5; step++)
                {
                    var observed = session.Poll();
                    Assert.That(observed.Phase, Is.EqualTo(UpdatePhase.Updating));
                    Assert.That(observed.Display.Paused, Is.True);
                    now = now.AddMinutes(10);
                }
            }
        }

        [TestCase(-6)]
        [TestCase(1)]
        public void InvalidReceiptCannotKeepCancelledSelectionAlive(int offsetSeconds)
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            var receipt = now.AddSeconds(offsetSeconds);
            using (var session = Open(() => new UpdateObservation { Phase = UpdatePhase.Ready,
                ReceivedAtUtc = receipt }, () => now, () => false))
            {
                session.SendClose(); session.Poll(); now = now.AddMinutes(1);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
            }
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void ContinuousObserverErrorsAfterCloseCannotBypassDataLossDeadline(bool accepted, bool freshReceipt)
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            using (var session = Open(() => new UpdateObservation {
                Phase = UpdatePhase.Error, Detail = "Synthetic observer error",
                ReceivedAtUtc = freshReceipt ? now : (DateTime?)null }, () => now, () => accepted, () => false))
            {
                Assert.That(session.SendClose(), Is.EqualTo(accepted));
                var initial = session.Poll();
                Assert.That(initial.Phase, Is.EqualTo(UpdatePhase.Error));
                Assert.That(initial.CancelPendingPlay, Is.False);
                now = now.AddSeconds(59);
                Assert.That(session.Poll().CancelPendingPlay, Is.False);
                now = now.AddSeconds(1);
                var expired = session.Poll();
                Assert.That(expired.Phase, Is.EqualTo(UpdatePhase.Error));
                Assert.That(expired.CancelPendingPlay, Is.True);
                Assert.That(session.SendCommand(UpdateCommand.Resume), Is.False);
            }
        }

        [Test]
        public void FreshCancelledObservationResetsAnErrorDeadlineWithoutRestoringPlay()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            bool error = true;
            using (var session = Open(() => new UpdateObservation {
                Phase = error ? UpdatePhase.Error : UpdatePhase.Ready, ReceivedAtUtc = now },
                () => now, () => false))
            {
                session.SendClose(); session.Poll();
                now = now.AddSeconds(59);
                error = false;
                var recovered = session.Poll();
                Assert.That(recovered.Phase, Is.EqualTo(UpdatePhase.Updating));
                Assert.That(recovered.Display.Stage, Is.EqualTo("Update complete"));
                Assert.That(recovered.CancelPendingPlay, Is.False);
                error = true;
                session.Poll();
                now = now.AddSeconds(59);
                Assert.That(session.Poll().CancelPendingPlay, Is.False);
                now = now.AddSeconds(1);
                Assert.That(session.Poll().CancelPendingPlay, Is.True);
            }
        }

        [Test]
        public void QueueRemovalConfirmationStillCompletesCloseWhenObserverReportsError()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            bool confirmed = false;
            using (var session = Open(() => new UpdateObservation { Phase = UpdatePhase.Error },
                () => now, () => true, () => confirmed))
            {
                session.SendClose(); session.Poll();
                now = now.AddMinutes(2);
                confirmed = true;
                var observed = session.Poll();
                Assert.That(observed.CloseConfirmed, Is.True);
                Assert.That(observed.CancelPendingPlay, Is.False);
                Assert.That(observed.Phase, Is.Not.EqualTo(UpdatePhase.Ready));
            }
        }

        [Test]
        public void CloseConfirmationExceptionUsesBoundedRecoveryWithoutAssumingRemoval()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            using (var session = Open(() => new UpdateObservation { Phase = UpdatePhase.Ready, ReceivedAtUtc = now },
                () => now, () => true, () => { throw new IOException("Synthetic confirmation failure"); }))
            {
                Assert.That(session.SendClose(), Is.True);
                var waiting = session.Poll();
                Assert.That(waiting.CloseConfirmed, Is.Not.True);
                Assert.That(waiting.Display.WaitingForData, Is.True);
                now = now.AddMinutes(1);
                var expired = session.Poll();
                Assert.That(expired.Phase, Is.EqualTo(UpdatePhase.Error));
                Assert.That(expired.CancelPendingPlay, Is.True);
                Assert.That(expired.CloseConfirmed, Is.Not.True);
            }
        }

        [Test]
        public void CloseConfirmationExceptionCanRecoverWithoutRevivingCancelledPlay()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            bool failed = true;
            using (var session = Open(() => new UpdateObservation { Phase = UpdatePhase.Ready, ReceivedAtUtc = now },
                () => now, () => true, () => {
                    if (failed) throw new IOException("Synthetic confirmation failure");
                    return true;
                }))
            {
                session.SendClose();
                Assert.That(session.Poll().CloseConfirmed, Is.Not.True);
                failed = false;
                var recovered = session.Poll();
                Assert.That(recovered.CloseConfirmed, Is.True);
                Assert.That(recovered.Phase, Is.EqualTo(UpdatePhase.Updating));
            }
        }

        [TestCase(null)]
        [TestCase("new-client")]
        public void SteamExitOrRestartCannotReleaseOriginalPlay(string replacementClient)
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            string client = "original-client";
            var lifetime = new SteamSelectedClientLifetime();
            var sample = SelectedSessionFailureTests.Frame(now);
            int reads = 0;
            using (var session = new SteamSelectedUpdateSession(550, new Observer(() => Missing()), () => {
                reads++;
                lifetime.Observe(client, true);
                return sample;
            }, () => SteamSelectionResult.Accepted, () => { }, () => { }, (s,w) => { }, utcNow: () => now))
            {
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                client = replacementClient;
                now = now.AddSeconds(1); sample = SelectedSessionFailureTests.Frame(now, 4);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                int afterFailure = reads;
                client = "original-client";
                now = now.AddSeconds(1); sample = SelectedSessionFailureTests.Frame(now, 4);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                Assert.That(reads, Is.EqualTo(afterFailure));
            }
        }
        [Test]
        public void ObserverFailureAllowsBoundedRecoveryAndCloseRetry()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            bool fail = true, confirmed = false;
            using (var session = Open(() => {
                if (fail) throw new IOException("injected disconnection");
                return new UpdateObservation { Phase = UpdatePhase.Ready, ReceivedAtUtc = now };
            }, () => now, () => confirmed, () => confirmed))
            {
                session.SendClose();
                Assert.That(session.Poll().Display.Stage, Is.EqualTo("Update data unavailable"));
                now = now.AddMinutes(1);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                fail = false; confirmed = true;
                Assert.That(session.SendClose(), Is.True);
                var recovered = session.Poll();
                Assert.That(recovered.CloseConfirmed, Is.True);
                Assert.That(recovered.Phase, Is.EqualTo(UpdatePhase.Updating));
            }
        }
    }
}
