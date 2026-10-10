using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("SelectedSession")]
    public sealed class SelectedSessionFailureTests
    {
        private sealed class Observer : IUpdateControlSession
        {
            public UpdatePhase Phase = UpdatePhase.Updating;
            public bool Paused, Disposed;
            public UpdateObservation Poll() { return new UpdateObservation { Phase = Phase,
                ReceivedAtUtc = DateTime.UtcNow, ControlPaused = Paused, CanPause = !Paused, CanResume = Paused,
                Display = new UpdateDisplay { Stage = "Downloading", Paused = Paused } }; }
            public bool SendCommand(UpdateCommand command) { Paused = command == UpdateCommand.Pause; return true; }
            public void Dispose() { Disposed = true; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedCloseKeepsObservedStateAllowsRetryAndCannotResumeSelection(bool throws)
        {
            int selections = 0, reads = 0, closes = 0;
            bool accept = false;
            var observer = new Observer();
            using (var session = new SteamSelectedUpdateSession(550, observer, () => { reads++; return Frame(DateTime.UtcNow, 4); },
                () => { selections++; return SteamSelectionResult.Accepted; }, () => { }, () => { }, (s, w) => { },
                close: () => { closes++; if (!accept && throws) throw new IOException("injected refusal"); if (accept) observer.Paused = true; return accept; },
                closeConfirmed: () => accept))
            {
                if (throws) Assert.Throws<IOException>(() => session.SendClose());
                else Assert.That(session.SendClose(), Is.False);
                Assert.That(session.Poll().Display.Paused, Is.False);
                observer.Phase = UpdatePhase.Ready;
                var complete = session.Poll();
                Assert.That(complete.Phase, Is.EqualTo(UpdatePhase.Updating));
                Assert.That(complete.Display.Paused, Is.False);
                Assert.That(complete.Display.Stage, Is.EqualTo("Update complete"));
                Assert.That(complete.CloseConfirmed, Is.Not.True);
                Assert.That(reads + selections, Is.Zero);
                accept = true;
                Assert.That(session.SendClose(), Is.True);
                Assert.That(session.Poll().CloseConfirmed, Is.True);
                Assert.That(session.Poll().Display.Paused, Is.True);
                Assert.That(closes, Is.EqualTo(2));
            }
        }

        [Test]
        public async Task DuplicateCloseCannotIssueAnotherCommandWhileFirstIsInFlight()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                int calls = 0;
                var observer = new Observer();
                using (var session = new SteamSelectedUpdateSession(550, observer, () => null,
                    () => SteamSelectionResult.Accepted, () => { }, () => release.Set(), (s, w) => { },
                    close: () => { Interlocked.Increment(ref calls); entered.Set(); release.WaitOne(3000); return true; }))
                {
                    var first = Task.Run(() => session.SendClose());
                    Assert.That(entered.WaitOne(1000), Is.True);
                    var second = Task.Run(() => session.SendClose());
                    try { Assert.That(second.Wait(1000), Is.True); }
                    finally { release.Set(); }
                    Assert.That(await first, Is.True);
                    Assert.That(await second, Is.False);
                    Assert.That(calls, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void DisposalDuringCloseCannotReturnSuccessfulAcknowledgement()
        {
            var observer = new Observer();
            SteamSelectedUpdateSession session = null;
            session = new SteamSelectedUpdateSession(550, observer, () => null, () => SteamSelectionResult.Accepted,
                () => { }, () => { }, (s, w) => { }, close: () => { session.Dispose(); return true; });
            using (session)
            {
                Assert.That(session.SendClose(), Is.False);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Idle));
                Assert.That(observer.Disposed, Is.True);
            }
        }

        [Test]
        public void ShortDataLossRecoversOnlyAfterTwoFreshCompletionFrames()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            SteamLiveSample sample = Frame(now);
            using (var session = new SteamSelectedUpdateSession(550, new Observer(), () => sample,
                () => SteamSelectionResult.Accepted, () => { }, () => { }, (s, w) => { }, utcNow: () => now))
            {
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                now = now.AddSeconds(6); sample = null;
                var missing = session.Poll();
                Assert.That(missing.Display.Stage, Is.EqualTo("Update data unavailable"));
                Assert.That(missing.Display.DownloadPercent, Is.Null);
                now = now.AddSeconds(1); sample = Frame(now, 4);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                now = now.AddSeconds(1); sample = Frame(now, 4);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Ready));
            }
        }

        [Test]
        public void ProlongedDataLossDisarmsPlayEvenWhenDataReturns()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            SteamLiveSample sample = Frame(now);
            int reads = 0;
            using (var session = new SteamSelectedUpdateSession(550, new Observer(), () => { reads++; return sample; },
                () => SteamSelectionResult.Accepted, () => { }, () => { }, (s, w) => { }, utcNow: () => now))
            {
                session.Poll();
                now = now.AddSeconds(61); sample = null;
                var failed = session.Poll();
                Assert.That(failed.Phase, Is.EqualTo(UpdatePhase.Error));
                Assert.That(failed.UserMessage, Does.Contain("press Play again"));
                int before = reads;
                sample = Frame(now, 4);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                Assert.That(reads, Is.EqualTo(before));
            }
        }

        [Test]
        public void LongFileProcessingWithFreshDataDoesNotExpireOrLaunchAtDownload100()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            using (var session = new SteamSelectedUpdateSession(550, new Observer(), () => Frame(now),
                () => SteamSelectionResult.Accepted, () => { }, () => { }, (s, w) => { }, utcNow: () => now))
            {
                for (int step = 0; step < 5; step++)
                {
                    Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                    now = now.AddMinutes(10);
                }
            }
        }

        [Test]
        public void InitialConnectionDeadlineRequiresNewExplicitPlay()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            SteamLiveSample sample = null;
            using (var session = new SteamSelectedUpdateSession(550, new Observer(), () => sample,
                () => SteamSelectionResult.Accepted, () => { }, () => { }, (s, w) => { }, utcNow: () => now))
            {
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                now = now.AddMinutes(3);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                sample = Frame(now, 4);
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
            }
        }

        [Test]
        public void UserSafeErrorTextDoesNotExposeDiagnosticDetail()
        {
            var state = new UpdateState { Phase = UpdatePhase.Error, Detail = "sensitive diagnostic fixture" };
            Assert.That(state.Text, Does.Not.Contain(state.Detail));
            state.UserMessage = "Update data unavailable — press Play again";
            Assert.That(state.Text, Is.EqualTo(state.UserMessage));
        }

        [Test]
        public void PauseIntentCannotInventPausedDisplayBeforeGlobalConfirmation()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            using (var session = new SteamSelectedUpdateSession(550, new Observer(), () => Frame(now, 4),
                () => SteamSelectionResult.Accepted, () => { }, () => { }, (s, w) => { },
                send: _ => true, utcNow: () => now))
            {
                session.Poll();
                Assert.That(session.SendCommand(UpdateCommand.Pause), Is.True);
                now = now.AddSeconds(1);
                var pending = session.Poll();
                Assert.That(pending.Phase, Is.EqualTo(UpdatePhase.Updating));
                Assert.That(pending.ControlPaused, Is.False);
                Assert.That(pending.Display.Paused, Is.False);
            }
        }

        [Test]
        public void MissingGlobalStateCannotKeepCompletedSelectionWaitingForever()
        {
            var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            SteamLiveSample sample = Frame(now, 4); sample.DownloadsEnabled = null;
            using (var session = new SteamSelectedUpdateSession(550, new Observer(), () => sample,
                () => SteamSelectionResult.Accepted, () => { }, () => { }, (s, w) => { }, utcNow: () => now))
            {
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                now = now.AddMinutes(3);
                sample = Frame(now, 4); sample.DownloadsEnabled = null;
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
                sample.DownloadsEnabled = true;
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Error));
            }
        }

        internal static SteamLiveSample Frame(DateTime receipt, uint flags = 270)
        { return new SteamLiveSample { AppId = 550, InstallState = flags, UpdateState = flags == 4 ? 0u : 97u,
            ReceivedAtUtc = receipt, DownloadsEnabled = true, HasCounters = flags != 4,
            Downloaded = 100, DownloadTotal = 100, Staged = 70, StageTotal = 100 }; }
    }
}
