using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class ConnectionRemovalTests
    {
        private string directory;
        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "ConnectionRemoval-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(directory, "steamclient64.dll"), "fixture; never load");
        }
        [TearDown]
        public void TearDown() { Directory.Delete(directory, true); }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedRemovalDoesNotDisableOrPurgeTheProvider(bool epic)
        {
            SteamUiAccess.Enable(directory);
            int applied = 0, saves = 0;
            var settings = new Settings { EpicEnabled = true };
            var model = new SettingsViewModel(settings, () => directory,
                findEpicFolder: _ => directory, epicConfigured: _ => true, epicHasBackup: () => true,
                disableAccess: _ => throw new IOException("Removal refused"),
                configureEpic: (root, remove) => throw new IOException("Removal refused"),
                save: _ => saves++, apply: () => applied++);
            model.InitializeLocation();
            await model.RefreshEpicLocationAsync();
            await (epic ? model.RemoveEpicConnectionAsync() : model.RemoveSteamConnectionAsync());
            Assert.That(settings.SteamConnectionRemoved || settings.EpicConnectionRemoved, Is.False);
            Assert.That(applied + saves, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExplicitSetupReenablesOnlyTheSelectedProvider(bool epic)
        {
            var settings = new Settings { EpicEnabled = true, SteamConnectionRemoved = true, EpicConnectionRemoved = true };
            int applied = 0, saves = 0;
            var model = new SettingsViewModel(settings, () => directory,
                findEpicFolder: _ => directory, epicConfigured: _ => false, epicHasBackup: () => false,
                enableAccess: root => { SteamUiAccess.Enable(root); return Task.CompletedTask; },
                configureEpic: (root, remove) => Task.CompletedTask,
                save: _ => saves++, apply: () => applied++, launcherSession: _ => "");
            model.InitializeLocation();
            await model.RefreshEpicLocationAsync();
            await (epic ? model.ConnectEpicAsync() : model.EnableLocalAccessAsync());
            Assert.That(epic ? settings.EpicConnectionRemoved : settings.SteamConnectionRemoved, Is.False);
            Assert.That(epic ? settings.SteamConnectionRemoved : settings.EpicConnectionRemoved, Is.True);
            Assert.That(saves, Is.EqualTo(1));
            Assert.That(applied, Is.EqualTo(1));
        }

        [Test]
        public void ClearingHistoryDeletesDiskDataAndRejectsEarlierCallbacks()
        {
            string path = Path.Combine(directory, "history.v1");
            var history = new SteamCoverHistory(path, (message, warning) => {});
            var sample = SteamQueueSample.Parse("Q3|1|0|0|1|550:6:-1|-", new uint[] { 550 });
            sample.ReceivedUtc = DateTime.UtcNow;
            var snapshot = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture") }, sample, null);
            history.Observe(snapshot);
            Assert.That(File.Exists(path), Is.True);
            history.Clear();
            history.Observe(snapshot);
            Assert.That(history.PendingAppIds, Is.Empty);
            Assert.That(File.Exists(path), Is.False);
            Assert.That(new SteamCoverHistory(path, (message, warning) => {}).PendingAppIds, Is.Empty);
            System.Threading.Thread.Sleep(20); // a new observation after the clear boundary
            sample.ReceivedUtc = DateTime.UtcNow;
            history.Observe(new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture") }, sample, null));
            Assert.That(history.PendingAppIds, Is.EqualTo(new uint[] { 550 }));
        }

        [Test]
        public async Task OldHistoryCanBeRemovedAfterTheMarkerWasAlreadyDeleted()
        {
            var settings = new Settings { EpicEnabled = true };
            var model = new SettingsViewModel(settings, () => directory,
                steamHasInformation: () => true,
                disableAccess: root => { File.Delete(Path.Combine(root,".cef-enable-remote-debugging")); return Task.CompletedTask; });
            model.InitializeLocation();
            Assert.That(model.SteamCanRemove, Is.True);
            await model.RemoveSteamConnectionAsync();
            Assert.That(settings.SteamConnectionRemoved, Is.True);
            Assert.That(model.CanEnableAccess, Is.True);
        }

        [Test]
        public async Task DelayedRemovalCannotDisableANewerInstallationSelection()
        {
            SteamUiAccess.Enable(directory);
            string other = Path.Combine(directory, "other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(other, "steamclient64.dll"), "fixture; never load");
            var completion = new TaskCompletionSource<bool>();
            var settings = new Settings { EpicEnabled = true, SteamFolder = directory };
            var model = new SettingsViewModel(settings, () => directory, disableAccess: _ => completion.Task);
            model.InitializeLocation();
            var removal = model.RemoveSteamConnectionAsync();
            model.FolderInput = other;
            completion.SetResult(true);
            await removal;
            Assert.That(settings.SteamConnectionRemoved, Is.False);
        }
    }
}
