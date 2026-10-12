using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Epic;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class EpicConnectionFailureTests
    {
        [TestCase(0,EpicConnectionFailure.PermissionDenied)]
        [TestCase(1,EpicConnectionFailure.TimedOut)]
        [TestCase(2,EpicConnectionFailure.ConnectionUnavailable)]
        [TestCase(3,EpicConnectionFailure.InvalidResponse)]
        [TestCase(4,EpicConnectionFailure.DiscoveryFailed)]
        public async Task NativeFailuresPreserveAnActionableCategoryWithoutPrivateMessages(int kind, EpicConnectionFailure expected)
        {
            Exception error = new Exception[] { new UnauthorizedAccessException("private account/path"), new TimeoutException("private account/path"),
                new HttpRequestException("private account/path"), new InvalidDataException("private account/path"), new InvalidOperationException("private account/path") }[kind];
            var logs = new List<string>();
            using (var catalog = new EpicUpdateCatalog(folder => { if (kind == 4) throw error; return new EpicEndpoint(1,1); },
                (endpoint,token) => Task.FromException<string>(error), (message,warning) => logs.Add(message)))
            {
                catalog.Configure(true,500);
                await WaitUntil(() => catalog.Current.Failure != EpicConnectionFailure.None);
                Assert.That(catalog.Current.Failure,Is.EqualTo(expected)); Assert.That(catalog.Current.Fresh,Is.False);
                Assert.That(catalog.Current.Status,Does.Not.Contain("private"));
                Assert.That(string.Join(" ",logs),Does.Not.Contain("private"));
            }
        }
        [Test]
        public async Task RecoveryClearsTheFailureAndDisabledObservationClearsItsDetails()
        {
            bool failing = true;
            using (var catalog = new EpicUpdateCatalog(folder => new EpicEndpoint(1,1),
                (endpoint,token) => failing ? Task.FromException<string>(new HttpRequestException("injected")) : Task.FromResult("{\"products\":[]}")))
            {
                catalog.Configure(true,500); await WaitUntil(() => catalog.Current.Failure != EpicConnectionFailure.None);
                catalog.Configure(false,500); Assert.That(catalog.Current.Failure,Is.EqualTo(EpicConnectionFailure.None));
                failing = false; catalog.Configure(true,500); await WaitUntil(() => catalog.Current.Fresh);
                Assert.That(catalog.Current.Failure,Is.EqualTo(EpicConnectionFailure.None));
            }
        }
        [Test]
        public async Task UnsupportedInterfaceDoesNotOfferAMisleadingRestart()
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => "fixture",
                epicConfigured: _ => true, epicHasBackup: () => true, launcherSession: _ => "running");
            model.EpicConnection = () => new EpicSnapshot(DateTime.MinValue,new EpicProduct[0],false,"ignored",EpicConnectionFailure.InterfaceUnavailable);
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicCard.Status,Is.EqualTo("Connection error")); Assert.That(model.EpicCard.Action,Is.Null);
            model.EpicObservationEnabled = false; Assert.That(model.EpicCard.Status,Is.EqualTo("Disabled"));
        }
        private static async Task WaitUntil(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddSeconds(2);
            while (!ready() && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.That(ready(),Is.True);
        }
    }
}
