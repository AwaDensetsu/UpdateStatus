using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Epic;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class ClosedLauncherConnectionTests
    {
        private string directory;
        [SetUp]
        public void CreateMetadataOnlyInstallation()
        {
            directory = Path.Combine(Path.GetTempPath(), "Closed-connection-"+Guid.NewGuid());
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(directory,"steamclient64.dll"), "fixture; never load");
        }
        [TearDown]
        public void Cleanup() { Directory.Delete(directory,true); }

        [TestCase(true,false,"Not running",true)]
        [TestCase(false,false,"Not connected",false)]
        [TestCase(true,true,"Not connected",false)]
        public void ClosedSteamUsesSavedSetupAndRespectsRemoval(bool configured, bool removed, string expected, bool canRemove)
        {
            if (configured) File.WriteAllText(Path.Combine(directory,".cef-enable-remote-debugging"),"");
            var model = new SettingsViewModel(new Settings { EpicEnabled = true, SteamConnectionRemoved = removed }, () => directory,
                launcherSession: _ => "");
            model.InitializeLocation();
            Assert.That(model.ConnectionStatus,Is.EqualTo(expected));
            Assert.That(model.SteamRemoveEnabled,Is.EqualTo(canRemove));
        }
        [Test]
        public void RunningSteamStillNeedsFreshNativeData()
        {
            SteamUiAccess.Enable(directory);
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => directory, launcherSession: _ => "running");
            model.InitializeLocation();
            Assert.That(model.ConnectionStatus,Is.EqualTo("Restart required"));
            Assert.That(model.SteamRemoveEnabled,Is.True);
        }
        [Test]
        public async Task SteamPendingChangeStopsRequiringRestartWhenItsProcessExits()
        {
            string session = "old";
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => directory,
                enableAccess: folder => { SteamUiAccess.Enable(folder); return Task.CompletedTask; },
                launcherSession: _ => session);
            model.InitializeLocation();
            await model.EnableLocalAccessAsync();
            Assert.That(model.ConnectionStatus,Is.EqualTo("Restart required"));
            session = ""; model.RefreshSteamLocation();
            Assert.That(model.ConnectionStatus,Is.EqualTo("Not running"));
            Assert.That(model.SteamRemoveEnabled,Is.True);
        }
        [Test]
        public async Task ClosedSteamCanRemoveSetupWithoutOpeningTheLauncher()
        {
            SteamUiAccess.Enable(directory);
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => directory, launcherSession: _ => "",
                disableAccess: folder => { File.Delete(Path.Combine(folder,".cef-enable-remote-debugging")); return Task.CompletedTask; });
            model.InitializeLocation();
            Assert.That(model.SteamRemoveEnabled,Is.True);
            await model.RemoveSteamConnectionAsync();
            Assert.That(model.ConnectionStatus,Is.EqualTo("Not connected"));
            Assert.That(model.Settings.SteamConnectionRemoved,Is.True);
        }
        [TestCase(true,false,"Not running",true)]
        [TestCase(false,false,"Not connected",false)]
        [TestCase(true,true,"Not connected",false)]
        public async Task ClosedEpicUsesValidatedSetupAndRespectsRemoval(bool configured, bool removed, string expected, bool canRemove)
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true, EpicConnectionRemoved = removed }, () => null,
                findEpicFolder: _ => directory, epicConfigured: _ => configured, epicHasBackup: () => configured,
                launcherSession: _ => "");
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicStatus,Is.EqualTo(expected));
            Assert.That(model.EpicRemoveEnabled,Is.EqualTo(canRemove));
        }
        [Test]
        public async Task RunningEpicStillRequiresLiveDataDespiteSavedSetup()
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => true, epicHasBackup: () => true, launcherSession: _ => "running");
            model.EpicConnection = () => new EpicSnapshot(DateTime.UtcNow.AddMinutes(-1),new EpicProduct[0],true,"");
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicStatus,Is.EqualTo("Restart required"));
            Assert.That(model.EpicRemoveEnabled,Is.True);
        }
        [Test]
        public async Task ClosingEpicAfterPendingSetupShowsSavedSetupAndRemovalDoesNotReopenIt()
        {
            bool configured = false;
            string session = "old";
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => configured, epicHasBackup: () => configured, launcherSession: _ => session,
                configureEpic: (_,remove) => { configured = !remove; return Task.CompletedTask; });
            await model.RefreshEpicLocationAsync(); await model.ConnectEpicAsync();
            Assert.That(model.EpicStatus,Is.EqualTo("Restart required"));
            session = ""; model.RefreshEpicConnection();
            Assert.That(model.EpicStatus,Is.EqualTo("Not running"));
            Assert.That(model.EpicRemoveEnabled,Is.True);
            await model.RemoveEpicConnectionAsync();
            Assert.That(model.EpicStatus,Is.EqualTo("Not connected"));
        }
        [Test]
        public async Task ExactCleanProtocolStillHasValidatedSavedEpicSetup()
        {
            string current = null;
            var setup = new EpicConnectionSetup(Path.Combine(directory,"journal.json"),
                discover: (folder,port) => new System.Collections.Generic.List<EpicStartupChange> {
                    new EpicStartupChange { Kind="protocol", Target="clean target", After="debug target", Before=null }
                }, read: _ => current, write: (_,value) => current=value);
            setup.Enable(directory); current="clean target";
            Assert.That(setup.Configured(directory),Is.False);
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: setup.Configured, epicHasBackup: () => setup.HasBackup,
                epicSetupReady: folder => setup.GetRestartArguments(folder).Length!=0, launcherSession: _ => "");
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicStatus,Is.EqualTo("Not running"));
            Assert.That(model.EpicShowSetup,Is.False);
            Assert.That(model.EpicRemoveEnabled,Is.True);
            Assert.That(current,Is.EqualTo("clean target"));
        }
        [Test]
        public async Task InvalidSavedEpicSetupCannotClaimClosedConnection()
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => false, epicHasBackup: () => true,
                epicSetupReady: _ => throw new IOException("Changed or foreign installation"), launcherSession: _ => "");
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicStatus,Is.EqualTo("Check the Epic connection setup"));
            Assert.That(model.EpicRemoveEnabled,Is.True);
        }
    }
}
