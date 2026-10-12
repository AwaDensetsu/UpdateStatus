using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Epic;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class ConnectionCardTests
    {
        [Test]
        public async Task ClosedEpicRejectsFreshReceiptUntilThereIsARunningLauncher()
        {
            string session = "";
            var model = Epic(() => session);
            model.EpicConnection = () => new EpicSnapshot(DateTime.UtcNow, new EpicProduct[0], true, "Connected");
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicCard.Status, Is.EqualTo("Not running"));
            Assert.That(model.EpicCard.Action, Is.EqualTo("Launch"));
            session = "running"; model.RefreshEpicConnection();
            Assert.That(model.EpicCard.Status, Is.EqualTo("Connected"));
            Assert.That(model.EpicCard.Action, Is.Null);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task ClosedCleanupRequiresConfirmation(bool accepted)
        {
            int removals = 0, confirmations = 0;
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => "fixture",
                epicConfigured: _ => true, epicHasBackup: () => true, launcherSession: _ => "",
                configureEpic: (_,remove) => { if (remove) removals++; return Task.CompletedTask; },
                confirmRemoval: _ => { confirmations++; return accepted; });
            await model.RefreshEpicLocationAsync();
            await model.ConfirmRemoveConnectionAsync(true);
            Assert.That(confirmations, Is.EqualTo(1));
            Assert.That(removals, Is.EqualTo(accepted ? 1 : 0));
            Assert.That(model.Settings.EpicConnectionRemoved, Is.EqualTo(accepted));
        }
        [Test]
        public async Task EpicDisabledDoesNotRemoveSetupAndImmediatelyBlocksActions()
        {
            var model = Epic(() => ""); await model.RefreshEpicLocationAsync();
            model.EpicObservationEnabled = false;
            Assert.That(model.EpicCard.Status, Is.EqualTo("Disabled"));
            Assert.That(model.EpicCard.ActionsAvailable, Is.False);
            Assert.That(model.EpicCard.CanRemove, Is.False);
            Assert.That(model.Settings.EpicConnectionRemoved, Is.False);
            model.EpicObservationEnabled = true;
            Assert.That(model.EpicCard.Action, Is.EqualTo("Launch"));
        }
        [Test]
        public async Task LaunchIsSerializedAndRequiresAReceiptFromAfterReopen()
        {
            string session = "";
            int launches = 0;
            var complete = new TaskCompletionSource<bool>();
            var model = Epic(() => session, (_,epic) => { launches++; return complete.Task; });
            var old = new EpicSnapshot(DateTime.UtcNow.AddMilliseconds(-100), new EpicProduct[0], true, "Connected");
            model.EpicConnection = () => old;
            await model.RefreshEpicLocationAsync();
            var pending = model.LaunchConfiguredLauncherAsync(true);
            Assert.That(model.EpicCard.Status, Is.EqualTo("Launching…"));
            Assert.That(model.EpicCard.ActionsAvailable, Is.False);
            Assert.That(model.EpicCard.CanRemove, Is.False);
            await model.LaunchConfiguredLauncherAsync(true);
            Assert.That(launches, Is.EqualTo(1));
            session = "new"; complete.SetResult(true); await pending;
            Assert.That(model.EpicCard.Status, Is.EqualTo("Connecting…"));
            model.EpicConnection = () => new EpicSnapshot(DateTime.UtcNow,new EpicProduct[0],true,"Connected");
            model.RefreshEpicConnection();
            Assert.That(model.EpicCard.Status, Is.EqualTo("Connected"));
        }
        [Test]
        public async Task LaunchFailureOffersAnActionableRetry()
        {
            var model = Epic(() => "", (_,epic) => Task.FromException(new IOException("Could not start launcher")));
            await model.RefreshEpicLocationAsync(); await model.LaunchConfiguredLauncherAsync(true);
            Assert.That(model.EpicCard.Status, Is.EqualTo("Connection error"));
            Assert.That(model.EpicCard.Description, Does.Contain("Could not start"));
            Assert.That(model.EpicCard.Action, Is.EqualTo("Launch"));
        }
        [Test]
        public async Task InvalidEpicFolderSelectionPreservesExistingPreference()
        {
            var settings = new Settings { EpicEnabled = true, EpicFolder = "valid" };
            var model = new SettingsViewModel(settings, () => null, browse: () => "invalid", findEpicFolder: path => path == "valid" ? "valid" : null,
                epicConfigured: _ => false, epicHasBackup: () => false, launcherSession: _ => "");
            await model.RefreshEpicLocationAsync(); model.BrowseEpicFolder();
            Assert.That(settings.EpicFolder, Is.EqualTo("valid"));
            Assert.That(model.EpicCard.Status, Is.EqualTo("Connection error"));
        }
        [Test]
        public async Task RemovingSetupShowsProgressAndBlocksAllConnectionActions()
        {
            var complete = new TaskCompletionSource<bool>();
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => "fixture",
                epicConfigured: _ => true, epicHasBackup: () => true, launcherSession: _ => "",
                configureEpic: (_,remove) => complete.Task);
            await model.RefreshEpicLocationAsync();
            var pending = model.RemoveEpicConnectionAsync();
            Assert.That(model.EpicCard.Status, Is.EqualTo("Removing…"));
            Assert.That(model.EpicCard.Busy, Is.True);
            Assert.That(model.EpicCard.CanRemove, Is.False);
            Assert.That(model.SteamCard.ActionsAvailable, Is.False);
            complete.SetResult(true); await pending;
            Assert.That(model.EpicCard.Busy, Is.False);
            Assert.That(model.EpicCard.Action, Is.EqualTo("Setup"));
        }
        private static SettingsViewModel Epic(Func<string> session, Func<string,bool,Task> launch = null)
        {
            return new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => "fixture",
                epicConfigured: _ => true, epicHasBackup: () => true, launcherSession: _ => session(), openAfterExit: launch);
        }
    }
}
