using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Epic;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class EpicOptInTests
    {
        [Test]
        public void FreshSettingsLeaveEpicOffAndSteamDefaultsUnchanged()
        {
            var settings = new Settings();
            Assert.That(settings.EpicEnabled, Is.False);
            Assert.That(settings.Enabled && settings.CoverBadges && settings.AutomaticOverlay, Is.True);
            Assert.That(settings.SteamConnectionRemoved || settings.EpicConnectionRemoved, Is.False);
            Assert.That(settings.PollingInterval, Is.EqualTo(1000));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExistingExplicitEpicChoiceIsNotOverriddenByTheViewModel(bool enabled)
        {
            var settings = new Settings { EpicEnabled = enabled };
            var model = new SettingsViewModel(settings, () => null, launcherSession: _ => "");
            Assert.That(model.Settings, Is.SameAs(settings));
            Assert.That(model.EpicObservationEnabled, Is.EqualTo(enabled));
        }

        [Test]
        public async Task FreshDisabledEpicCannotSetUpRestartOrLaunch()
        {
            int changes = 0, launches = 0, restarts = 0;
            var model = new SettingsViewModel(new Settings(), () => null,
                findEpicFolder: _ => "fixture", epicConfigured: _ => false, epicHasBackup: () => false,
                launcherSession: _ => "",
                configureEpic: (_, remove) => { changes++; return Task.CompletedTask; },
                restartEpic: _ => { restarts++; return Task.CompletedTask; },
                openAfterExit: (_, epic) => { launches++; return Task.CompletedTask; });
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicCard.Status, Is.EqualTo("Disabled"));
            Assert.That(model.EpicCanConnect || model.EpicCanRestart || model.EpicCard.ActionsAvailable, Is.False);
            await model.ConnectEpicAsync();
            await model.RestartEpicAsync();
            await model.LaunchConfiguredLauncherAsync(true);
            Assert.That(changes + launches + restarts, Is.Zero);
        }

        [Test]
        public async Task DisabledDefaultNeverStartsEndpointDiscoveryOrReads()
        {
            int discoveries = 0, reads = 0;
            using (var catalog = new EpicUpdateCatalog(folder => {
                Interlocked.Increment(ref discoveries); return new EpicEndpoint(1, 1);
            }, (endpoint, token) => {
                Interlocked.Increment(ref reads); return Task.FromResult("{\"products\":[]}");
            }))
            {
                var settings = new Settings();
                catalog.Configure(settings.Enabled && settings.EpicEnabled, settings.EpicPollingInterval);
                await Task.Delay(600);
                Assert.That(discoveries, Is.Zero);
                Assert.That(reads, Is.Zero);
                Assert.That(catalog.Current.Fresh, Is.False);
            }
        }

        [Test]
        public async Task EnablingObservationDoesNotModifySetupUntilExplicitConnection()
        {
            int changes = 0;
            var model = new SettingsViewModel(new Settings(), () => null,
                findEpicFolder: _ => "fixture", epicConfigured: _ => false, epicHasBackup: () => false,
                launcherSession: _ => "",
                configureEpic: (_, remove) => { changes++; return Task.CompletedTask; });
            await model.RefreshEpicLocationAsync();
            model.EpicObservationEnabled = true;
            Assert.That(changes, Is.Zero);
            Assert.That(model.EpicCanConnect, Is.True);
            await model.ConnectEpicAsync();
            Assert.That(changes, Is.EqualTo(1));
        }
    }
}
