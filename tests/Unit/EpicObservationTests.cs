using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Playnite.SDK.Models;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers;
using PlayniteUpdateStatus.Launchers.Epic;
using PlayniteUpdateStatus.UI;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class EpicObservationTests
    {
        private static EpicProduct Product(bool? updating = false, bool? queued = false, bool? hasUpdate = false,
            bool? running = false, bool initialized = true, bool installed = true, string ns = "ns") =>
            new EpicProduct("app", ns, "catalog", initialized, installed, hasUpdate, updating, queued, running);
        private static EpicSnapshot Snapshot(params EpicProduct[] products) =>
            new EpicSnapshot(DateTime.UtcNow, products, true, "Connected");

        [Test]
        public void NativeAppNameRequiresTheOfficialImportingLibrary()
        {
            var game = new Game { PluginId = Guid.NewGuid(), GameId = "app", Name = "same title" };
            Assert.That(EpicSnapshot.Resolve(game), Is.Null);
            game.PluginId = EpicSnapshot.LibraryId;
            Assert.That(EpicSnapshot.Resolve(game).LauncherId, Is.EqualTo("app"));
        }
        [Test]
        public void DifferentProductTriplesWithOneAppNameAreAmbiguous()
        {
            var state = Snapshot(Product(ns:"a"), Product(ns:"b"), Product(ns:"a"));
            Assert.That(state.Find("app"), Is.Null);
        }
        [TestCase(-6)]
        [TestCase(6)]
        public void StaleOrFutureSamplesAreRejected(int offset)
        {
            var state = new EpicSnapshot(DateTime.UtcNow.AddSeconds(offset), new[] { Product(updating:true) }, true, "");
            Assert.That(state.Find("app"), Is.Null);
        }
        [Test]
        public void DisconnectedStateCannotExposeProducts()
        {
            var state = new EpicSnapshot(DateTime.UtcNow, new[] { Product(updating:true) }, false, "");
            Assert.That(state.Find("app"), Is.Null);
        }
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        public void NativeUpdateFlagsShowAttentionWithoutInventingCounters(bool updating, bool queued, bool required)
        {
            using (var session = new EpicUpdateSession("app", () => Snapshot(Product(updating, queued, required))))
            {
                var result = session.Poll();
                Assert.That(result.Phase, Is.EqualTo(UpdatePhase.Updating));
                Assert.That(result.Percent, Is.Null);
                Assert.That(result.Display.WorkPercent, Is.Null);
                Assert.That(result.Display.Downloaded, Is.Null);
                Assert.That(result.CanPause || result.CanResume, Is.False);
            }
        }
        [Test]
        public void AnIdlePleaseWaitTextOrRawProgressDoesNotBecomeAnUpdate()
        {
            var state = EpicSnapshot.Parse("{\"products\":[{\"app\":\"app\",\"namespace\":\"ns\",\"catalog\":\"catalog\",\"initialized\":true,\"installed\":true,\"hasUpdate\":false,\"updating\":false,\"queued\":false,\"running\":false,\"updatestatus\":\"Please Wait...\",\"progress\":100}]}", DateTime.UtcNow);
            using (var session = new EpicUpdateSession("app", () => state))
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Idle));
        }
        [Test]
        public void UpdateDisappearanceNeverEmitsReadyOrAutomaticPlay()
        {
            var state = Snapshot(Product(updating:true));
            using (var session = new EpicUpdateSession("app", () => state))
            {
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                state = Snapshot(Product());
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Idle));
                Assert.That(typeof(IUpdateControlSession).IsAssignableFrom(session.GetType()), Is.False);
            }
        }
        [Test]
        public void UninitializedUnknownAndMissingFlagsAreNotCompletion()
        {
            foreach (var product in new[] { Product(initialized:false), Product(installed:false), Product(updating:null) })
                using (var session = new EpicUpdateSession("app", () => Snapshot(product)))
                    Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.WaitingForData));
        }
        [Test]
        public void RunningProductHidesItsObserver()
        {
            using (var session = new EpicUpdateSession("app", () => Snapshot(Product(running:true))))
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Running));
        }
        [Test]
        public void DisconnectAndDisposeDiscardPreviousActiveState()
        {
            var state = Snapshot(Product(updating:true));
            var session = new EpicUpdateSession("app", () => state);
            Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
            state = new EpicSnapshot(DateTime.MinValue, new EpicProduct[0], false, "");
            Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.WaitingForData));
            session.Dispose();
            Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Idle));
        }
        [TestCase("stale")]
        [TestCase("disconnected")]
        [TestCase("missing")]
        [TestCase("uninitialized")]
        [TestCase("unknown-running")]
        [TestCase("unknown-update")]
        [TestCase("not-installed")]
        public void UnavailableEpicDataUsesNeutralWaitingWithoutInventingProgressOrCancellingPlay(string kind)
        {
            var state = kind == "stale" ? new EpicSnapshot(DateTime.UtcNow.AddMinutes(-1), new[] { Product(updating:true) }, true, "") :
                kind == "disconnected" ? new EpicSnapshot(DateTime.MinValue, new EpicProduct[0], false, "") :
                kind == "missing" ? Snapshot() : kind == "uninitialized" ? Snapshot(Product(initialized:false)) :
                kind == "unknown-running" ? Snapshot(Product(running:null)) :
                kind == "unknown-update" ? Snapshot(Product(updating:null)) : Snapshot(Product(installed:false));
            using (var session = new EpicUpdateSession("app", () => state))
            {
                var reply = session.Poll();
                Assert.That(reply.Phase, Is.EqualTo(UpdatePhase.WaitingForData));
                Assert.That(reply.UserMessage, Does.StartWith("Waiting for Epic"));
                Assert.That(reply.Percent, Is.Null);
                Assert.That(reply.Display, Is.Null);
                Assert.That(reply.CancelPendingPlay, Is.False);
                Assert.That(reply.CanPause || reply.CanResume, Is.False);
            }
        }
        [Test]
        public void WaitingSessionRecoversToFreshUpdateAndThenRunning()
        {
            var state = new EpicSnapshot(DateTime.MinValue, new EpicProduct[0], false, "");
            using (var session = new EpicUpdateSession("app", () => state))
            {
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.WaitingForData));
                state = Snapshot(Product(updating:true));
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Updating));
                state = Snapshot(Product(running:true));
                Assert.That(session.Poll().Phase, Is.EqualTo(UpdatePhase.Running));
            }
        }
        [Test]
        public void NeutralWaitingTextDoesNotReportAnUpdateFailure()
        {
            var state = new UpdateState { Phase = UpdatePhase.WaitingForData,
                UserMessage = "Waiting for Epic update data…" };
            Assert.That(state.Visible, Is.True);
            Assert.That(state.Text, Is.EqualTo(state.UserMessage));
            Assert.That(state.Text, Does.Not.Contain("interrupted"));
            Assert.That(state.Text, Does.Not.Contain("%"));
        }
        [Test]
        public void MonitorKeepsWaitingEpicSessionAndRecoversWithoutAnErrorWarning()
        {
            var state = Snapshot(Product(updating:true));
            int warnings = 0;
            using (var updating = new ManualResetEvent(false))
            using (var waiting = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((text, warning) => { if (warning) Interlocked.Increment(ref warnings); }))
            {
                var id = Guid.NewGuid();
                monitor.Changed += observed => {
                    if (observed.Phase == UpdatePhase.Updating) updating.Set();
                    if (observed.Phase == UpdatePhase.WaitingForData) waiting.Set();
                };
                monitor.Start(new LaunchIdentity { GameId = id, LauncherName = "Epic Games" },
                    new EpicUpdateSession("app", () => Volatile.Read(ref state)), 500);
                Assert.That(updating.WaitOne(2000), Is.True);
                updating.Reset();
                Volatile.Write(ref state, new EpicSnapshot(DateTime.MinValue, new EpicProduct[0], false, ""));
                Assert.That(waiting.WaitOne(2000), Is.True);
                Assert.That(monitor.Current.Phase, Is.EqualTo(UpdatePhase.WaitingForData));
                Volatile.Write(ref state, Snapshot(Product(updating:true)));
                Assert.That(updating.WaitOne(2000), Is.True);
                Assert.That(monitor.Current.GameId, Is.EqualTo(id));
                Assert.That(Volatile.Read(ref warnings), Is.Zero);
            }
        }
        [TestCase("missing-game")]
        [TestCase("other-library")]
        [TestCase("empty-id")]
        [TestCase("whitespace-id")]
        [TestCase("running")]
        public void CoverBadgeRejectsInvalidIdentityAndRunningGames(string kind)
        {
            var game = kind == "missing-game" ? null : new Game {
                PluginId = kind == "other-library" ? Guid.NewGuid() : EpicSnapshot.LibraryId,
                GameId = kind == "empty-id" ? "" : kind == "whitespace-id" ? "  " : "app",
                IsRunning = kind == "running"
            };
            Assert.That(Snapshot(Product(updating:true)).Badge(game), Is.Null);
        }
        [Test]
        public void SharedCoverPolicyCanPresentEpicWithoutMappingAnotherLibrary()
        {
            var state = Snapshot(Product(updating:true));
            var policy = new CoverBadgeSnapshot(null, null, null, state.Badge);
            var game = new Game { PluginId = EpicSnapshot.LibraryId, GameId = "app" };
            Assert.That(policy.Find(game).CoverText, Is.EqualTo("Updating"));
            game.PluginId = Guid.NewGuid();
            Assert.That(policy.Find(game), Is.Null);
        }
        [TestCase("ws://127.0.0.1:6000/devtools/page/one", true)]
        [TestCase("ws://127.0.0.1:6001/devtools/page/one", false)]
        [TestCase("ws://localhost:6000/devtools/page/one", false)]
        [TestCase("ws://192.168.1.2:6000/devtools/page/one", false)]
        [TestCase("ws://127.0.0.1:6000/devtools/worker/one", false)]
        [TestCase("ws://user@127.0.0.1:6000/devtools/page/one", false)]
        [TestCase("wss://127.0.0.1:6000/devtools/page/one", false)]
        public void SocketMustStayOnVerifiedLoopbackPage(string url, bool allowed)
        { Assert.That(EpicCefClient.IsAllowedSocket(new Uri(url),6000), Is.EqualTo(allowed)); }
        [Test]
        public void DisabledCatalogInvalidatesDataImmediatelyWithoutDiscovery()
        {
            using (var catalog = new EpicUpdateCatalog())
            {
                catalog.Configure(false, 1000);
                Assert.That(catalog.Current.Fresh, Is.False);
                Assert.That(catalog.Current.Status, Is.EqualTo("Epic monitoring disabled"));
            }
        }
        [Test]
        public void SteamRegistrationRemainsTheDefaultAndEpicAddsOnlyObservation()
        {
            Assert.That(LauncherCatalog.Create((s,w) => {}).Resolve(new Game { PluginId = EpicSnapshot.LibraryId, GameId = "app" }, null), Is.Null);
            using (var catalog = new EpicUpdateCatalog())
            {
                var registry = LauncherCatalog.Create((s,w) => {}, catalog);
                var match = registry.Resolve(new Game { PluginId = EpicSnapshot.LibraryId, GameId = "app" }, null);
                Assert.That(match.Registration.Capabilities, Is.EqualTo(UpdateCapabilities.Phases));
            }
        }
        [Test]
        public void CloseHidesEpicCardWithoutAControlCommandOrQueueRemoval()
        {
            using (var observed = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((s,w) => {}))
            {
                var session = new EpicUpdateSession("app", () => Snapshot(Product(updating:true)));
                var id = Guid.NewGuid();
                monitor.Changed += state => { if (state.Phase == UpdatePhase.Updating) observed.Set(); };
                var start = monitor.Start(new LaunchIdentity { GameId = id, LauncherName = "Epic Games" }, session, 500);
                Assert.That(observed.WaitOne(2000), Is.True);
                Assert.That(monitor.CanRemoveForReplacement, Is.False);
                Assert.That(monitor.RequestClose(id, start.SessionId), Is.True);
                Assert.That(monitor.Current.Dismissed, Is.True);
                Assert.That(monitor.Current.CommandPending, Is.False);
            }
        }
        [Test]
        public void EpicPollingValidationHasItsOwnInterval()
        {
            var settings = new Settings { EpicPollingInterval = 50 };
            var model = new SettingsViewModel(settings, () => null);
            System.Collections.Generic.List<string> errors;
            Assert.That(model.VerifySettings(out errors), Is.False);
            Assert.That(errors, Has.Some.Contains("Epic polling"));
        }
        [TestCase("{}")]
        [TestCase("{\"products\":{}}")]
        [TestCase("{\"products\":[0]}")]
        public void MalformedNativeResponseIsRejected(string text)
        { Assert.Throws<InvalidDataException>(() => EpicSnapshot.Parse(text, DateTime.UtcNow)); }
    }
}
