using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class ObserverNotificationTests
    {
        private sealed class Session : IUpdateCloseSession
        {
            public bool Paused, Disposed, ThrowOnDispose;
            public int Commands;
            public UpdateObservation Poll() { return new UpdateObservation { Phase = UpdatePhase.Updating, CanPause = !Paused, CanResume = Paused, ControlPaused = Paused }; }
            public bool SendCommand(UpdateCommand command) { Commands++; Paused = command == UpdateCommand.Pause; return true; }
            public bool SendClose() { Commands++; return true; }
            public void Dispose() { Disposed = true; if (ThrowOnDispose) throw new InvalidOperationException("injected disposal failure"); }
        }
        private static void Field(object target, string name, object value)
        { target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value); }
        private static void Tick(object target)
        {
            try { target.GetType().GetMethod("Tick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,new object[] { 1L }); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static UpdateMonitor Monitor(Session session, Guid game)
        {
            var monitor = new UpdateMonitor((message,warning) => { });
            // Controlled worker state: no timer is started before the regression is fixed.
            Field(monitor,"session",session); Field(monitor,"game",new LaunchIdentity { GameId = game }); Field(monitor,"generation",1L);
            Field(monitor,"current",new UpdateState { GameId = game, SessionId = 1, Phase = UpdatePhase.Updating, CanPause = true, ControlPaused = false });
            return monitor;
        }
        [Test]
        public void ThrowingStateSubscriberAbortsOnlyItsSessionAndStillNotifiesCleanup()
        {
            var session = new Session(); var game = Guid.NewGuid();
            using (var monitor = Monitor(session,game))
            {
                int cleanup = 0;
                Action<UpdateState> broken = state => { throw new InvalidOperationException("injected observer failure"); };
                monitor.Changed += broken;
                monitor.Changed += state => { if (state.Phase == UpdatePhase.Idle) cleanup++; };
                try
                {
                    Assert.DoesNotThrow(() => Tick(monitor));
                    Assert.That(monitor.Current.Phase,Is.EqualTo(UpdatePhase.Idle));
                    Assert.That(session.Disposed,Is.True);
                    Assert.That(session.Commands,Is.Zero);
                    Assert.That(cleanup,Is.EqualTo(1));
                }
                finally { monitor.Changed -= broken; }
            }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void FailedControlNotificationNeverSendsANativeCommand(bool close)
        {
            var session = new Session(); var game = Guid.NewGuid();
            using (var monitor = Monitor(session,game))
            {
                if (close) monitor.CloseRequested += (id,token) => { throw new InvalidOperationException("injected close owner failure"); };
                else monitor.CommandRequested += (id,token,command) => { throw new InvalidOperationException("injected command owner failure"); };
                bool accepted = true;
                Assert.DoesNotThrow(() => accepted = close ? monitor.RequestClose(game,1) : monitor.RequestCommand(game,UpdateCommand.Pause));
                Assert.That(accepted,Is.False); Assert.That(session.Commands,Is.Zero);
                Assert.That(monitor.Current.Phase,Is.EqualTo(UpdatePhase.Idle));
            }
        }
        [Test]
        public void FailedConfirmationCannotReleaseARetainedLaunch()
        {
            var session = new Session { Paused = true }; var game = Guid.NewGuid();
            using (var monitor = Monitor(session,game))
            {
                Field(monitor,"requestedCommand",(UpdateCommand?)UpdateCommand.Pause); Field(monitor,"commandSent",true);
                monitor.CommandConfirmed += (id,token,command) => { throw new InvalidOperationException("injected confirmation failure"); };
                Assert.DoesNotThrow(() => Tick(monitor));
                Assert.That(monitor.Current.Phase,Is.EqualTo(UpdatePhase.Idle));
                Assert.That(session.Disposed,Is.True);
            }
        }
        [Test]
        public void IdleNotificationFailureStillDisposesTheSession()
        {
            var session = new Session(); var game = Guid.NewGuid();
            using (var monitor = Monitor(session,game))
            {
                Action<UpdateState> broken = state => { throw new InvalidOperationException("injected cleanup failure"); };
                monitor.Changed += broken;
                try { Assert.DoesNotThrow(() => monitor.Stop(game)); Assert.That(session.Disposed,Is.True); Assert.That(monitor.Current.Phase,Is.EqualTo(UpdatePhase.Idle)); }
                finally { monitor.Changed -= broken; }
            }
        }
        [Test]
        public void DisposalFailureCannotLeavePlayEligibleAfterNotificationFailure()
        {
            var session = new Session { ThrowOnDispose = true }; var game = Guid.NewGuid();
            using (var monitor = Monitor(session,game))
            {
                Action<UpdateState> broken = state => { throw new InvalidOperationException("injected state failure"); };
                monitor.Changed += broken;
                try { Assert.DoesNotThrow(() => Tick(monitor)); Assert.That(monitor.Current.Phase,Is.EqualTo(UpdatePhase.Idle)); }
                finally { monitor.Changed -= broken; }
            }
        }
        [Test]
        public void StaleFailingNotificationCannotStopAReplacementGame()
        {
            var old = Guid.NewGuid(); var replacement = Guid.NewGuid();
            using (var monitor = Monitor(new Session(),old))
            {
                Action<UpdateState> broken = null;
                broken = state => {
                    monitor.Changed -= broken;
                    monitor.Start(new LaunchIdentity { GameId = replacement },new Session(),500);
                    throw new InvalidOperationException("old notification failed after replacement");
                };
                monitor.Changed += broken;
                Assert.DoesNotThrow(() => Tick(monitor));
                Assert.That(monitor.Current.GameId,Is.EqualTo(replacement));
            }
        }
        [Test]
        public void RealTimerNotificationFailureCancelsTheRetainedSdkCallback()
        {
            var session = new Session(); var game = new Playnite.SDK.Models.Game { Id = Guid.NewGuid(), Name = "fixture" };
            var args = new Playnite.SDK.Events.OnGameStartingEventArgs();
            typeof(Playnite.SDK.Events.OnGameStartingEventArgs).GetProperty("Game").SetValue(args,game,null);
            using (var cleanup = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((message,warning) => { }))
            {
                monitor.Changed += state => { throw new InvalidOperationException("injected timer observer"); };
                monitor.Changed += state => { if (state.Phase == UpdatePhase.Idle && session.Disposed) cleanup.Set(); };
                var start = monitor.Start(new LaunchIdentity { GameId = game.Id },session,500);
                Assert.That(cleanup.WaitOne(2000),Is.True);
                var retained = new PlayniteUpdateStatus.UI.SelectedLaunchGate(args,start.SessionId);
                Assert.That(retained.Wait(monitor,System.Windows.Threading.Dispatcher.CurrentDispatcher),Is.False);
                Assert.That(args.CancelStartup,Is.True); Assert.That(session.Commands,Is.Zero);
            }
        }
        private sealed class Reader : ISteamQueueReader
        {
            public string Notice => null;
            public SteamQueueSample TryRead() { var sample = SteamQueueSample.Parse("Q3|1|1|1|1|1:270:0|-",new uint[] { 1 }); sample.ReceivedUtc = DateTime.UtcNow; return sample; }
            public void Dispose() { }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void CatalogSubscriberFailureCannotBecomeAReadFailureOrEscapeRecovery(bool readFails)
        {
            using (var catalog = new SteamUpdateCatalog(() => "running", () => {
                if (readFails) throw new InvalidOperationException("injected discovery failure");
                return new List<SteamCatalogApp> { new SteamCatalogApp(1,"fixture") };
            }, apps => new Reader(), (message,warning) => { }))
            {
                Field(catalog,"timer",new Timer(_ => { },null,Timeout.Infinite,Timeout.Infinite)); Field(catalog,"generation",1L);
                int delivered = 0;
                Action<SteamCatalogSnapshot> broken = state => { throw new InvalidOperationException("injected catalog subscriber"); };
                catalog.Changed += broken; catalog.Changed += state => delivered++;
                try
                {
                    Assert.DoesNotThrow(() => Tick(catalog));
                    Assert.That(catalog.Current.Live,Is.EqualTo(!readFails));
                    Assert.That(delivered,Is.EqualTo(1));
                }
                finally { catalog.Changed -= broken; }
            }
        }
    }
}
