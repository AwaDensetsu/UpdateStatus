using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("ReaderLifetime")]
    public sealed class SharedReaderTests
    {
        private static IUpdateSession Observe(Func<SteamLiveSample> source)
        {
            var adapter = new SteamUpdateAdapter((message, warning) => { });
            var factory = typeof(SteamUpdateAdapter).GetMethod("OpenSelected", BindingFlags.NonPublic | BindingFlags.Instance);
            var game = new LaunchIdentity { GameId = Guid.NewGuid(), LauncherId = "550" };
            // This path deliberately has no installation; valid borrowed frames
            // must not depend on another Steam process or local manifest lookup.
            string root = Path.Combine(Path.GetTempPath(), "SharedReaderFixture-" + Guid.NewGuid());
            return (IUpdateSession)factory.Invoke(adapter, new object[] { game, root, source });
        }

        private static SteamLiveSample Frame(ulong bytes)
        {
            return new SteamLiveSample { AppId = 550, InstallState = 270, UpdateState = 97,
                HasCounters = true, Downloaded = bytes, DownloadTotal = 100, Staged = 70, StageTotal = 100,
                UiWorkPercent = 70, DownloadsEnabled = true, ReceivedAtUtc = DateTime.UtcNow };
        }

        [Test]
        public void BorrowedProgressAdvancesAndPausedMetadataRemainsUsableWithoutCounters()
        {
            var sample = Frame(50);
            using (var session = Observe(() => sample))
            {
                Assert.That(session.Poll().Display.DownloadPercent, Is.EqualTo(50));
                sample = Frame(75);
                var next = session.Poll();
                Assert.That(next.Display.DownloadPercent, Is.EqualTo(75));
                Assert.That(next.Display.WorkPercent, Is.EqualTo(70));
                sample = new SteamLiveSample { AppId = 550, InstallState = 14, Result = 4,
                    DownloadsEnabled = false, ReceivedAtUtc = DateTime.UtcNow };
                var paused = session.Poll();
                Assert.That(paused.Phase, Is.EqualTo(UpdatePhase.Updating));
                Assert.That(paused.Display.Paused, Is.True);
                Assert.That(paused.Display.Stage, Is.EqualTo("Paused"));
                Assert.That(paused.Display.Downloaded, Is.Null);
                Assert.That(paused.ReceivedAtUtc, Is.EqualTo(sample.ReceivedAtUtc));
            }
        }

        [Test]
        public void MissingForeignExpiredAndFutureBorrowedFramesCannotReuseProgress()
        {
            var sample = Frame(50);
            using (var session = Observe(() => sample))
            {
                Assert.That(session.Poll().Display.DownloadPercent, Is.EqualTo(50));
                var foreign = Frame(60); foreign.AppId = 551;
                var expired = Frame(60); expired.ReceivedAtUtc = DateTime.UtcNow.AddSeconds(-10);
                var future = Frame(60); future.ReceivedAtUtc = DateTime.UtcNow.AddSeconds(10);
                foreach (var invalid in new[] { null, foreign, expired, future })
                {
                    sample = invalid;
                    var state = session.Poll();
                    Assert.That(state.Display.WaitingForData, Is.True);
                    Assert.That(state.Display.Downloaded, Is.Null);
                    Assert.That(state.Percent, Is.Null);
                    Assert.That(state.ReceivedAtUtc, Is.Null);
                }
            }
        }

        [Test]
        public void DisposedObservationStopsReadingWithoutDestroyingTheOwnersSource()
        {
            int reads = 0;
            Func<SteamLiveSample> source = () => { reads++; return Frame(50); };
            var session = Observe(source);
            Assert.That(session.Poll().Display.DownloadPercent, Is.EqualTo(50));
            session.Dispose();
            int afterDispose = reads;
            Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Idle));
            Assert.That(reads, Is.EqualTo(afterDispose));
            Assert.That(source().Downloaded, Is.EqualTo(50));
            session.Dispose();
        }
    }
}
