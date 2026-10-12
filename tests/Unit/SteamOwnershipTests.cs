using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class SteamOwnershipTests
    {
        private string root, marker, journal;
        [SetUp]
        public void CreateFixture()
        {
            root = Path.Combine(Path.GetTempPath(),"Steam-ownership-"+Guid.NewGuid()); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"steam.exe"),"fixture; never execute");
            File.WriteAllText(Path.Combine(root,"steamclient64.dll"),"fixture; never load");
            marker = Path.Combine(root,".cef-enable-remote-debugging"); journal = Path.Combine(root,".update-status-connection.json");
        }
        [TearDown] public void Cleanup() { Directory.Delete(root,true); }
        [Test]
        public void CreatedMarkerHasOwnershipAndIsRemovedIdempotently()
        {
            var setup = new SteamConnectionSetup(root); setup.Enable();
            Assert.That(File.Exists(journal),Is.True); Assert.That(new FileInfo(marker).Length,Is.Zero);
            Assert.That(setup.Remove(),Is.True); Assert.That(File.Exists(marker)||File.Exists(journal),Is.False);
            Assert.That(setup.Remove(),Is.True);
        }
        [TestCase(false)] [TestCase(true)]
        public void ExistingFilesAndFoldersAreNeverAdoptedOrDeleted(bool folder)
        {
            if (folder) Directory.CreateDirectory(marker); else File.WriteAllText(marker,"");
            var setup = new SteamConnectionSetup(root); setup.Enable();
            Assert.That(File.Exists(journal),Is.False); Assert.That(setup.Remove(),Is.False);
            Assert.That(SteamUiAccess.Configured(root),Is.True);
        }
        [Test]
        public void ReplacementCannotInheritTheOldMarkersOwnership()
        {
            var setup = new SteamConnectionSetup(root); setup.Enable();
            File.Delete(marker); File.WriteAllText(marker,""); File.SetLastWriteTimeUtc(marker,DateTime.UtcNow.AddMinutes(-1));
            Assert.That(setup.Remove(),Is.False); Assert.That(File.Exists(marker),Is.True);
        }
        [Test]
        public void MarkerWithAddedContentsIsPreserved()
        {
            var setup = new SteamConnectionSetup(root); setup.Enable(); File.WriteAllText(marker,"external edit");
            Assert.That(setup.Remove(),Is.False); Assert.That(File.ReadAllText(marker),Is.EqualTo("external edit"));
        }
        [Test]
        public void MalformedOwnershipNeverAuthorizesDeletion()
        {
            var setup = new SteamConnectionSetup(root); setup.Enable(); File.WriteAllText(journal,"{}");
            Assert.Throws<IOException>(() => setup.Remove()); Assert.That(File.Exists(marker),Is.True);
        }
        [Test]
        public async Task RepeatedConcurrentSetupKeepsTheWinningOwnershipRecord()
        {
            await Task.WhenAll(Task.Run(() => new SteamConnectionSetup(root).Enable()),Task.Run(() => new SteamConnectionSetup(root).Enable()));
            Assert.That(new SteamConnectionSetup(root).Remove(),Is.True);
        }
        [Test]
        public void WindowsPathCasingDoesNotLoseOwnership()
        {
            new SteamConnectionSetup(root).Enable();
            Assert.That(new SteamConnectionSetup(root.ToUpperInvariant()).Remove(),Is.True);
        }
        [Test]
        public void AUserReadonlyAttributeChangeIsPreserved()
        {
            var setup = new SteamConnectionSetup(root); setup.Enable(); File.SetAttributes(marker,FileAttributes.ReadOnly);
            try { Assert.That(setup.Remove(),Is.False); Assert.That(File.Exists(marker),Is.True); }
            finally { File.SetAttributes(marker,FileAttributes.Normal); }
        }
        [Test]
        public async Task LegacySetupIsKeptButPluginObservationIsExplicitlyDisabled()
        {
            File.WriteAllText(marker,""); int applied = 0;
            var model = new SettingsViewModel(new Settings(), () => root, apply: () => applied++, launcherSession: _ => "");
            model.InitializeLocation(); await model.RemoveSteamConnectionAsync();
            Assert.That(File.Exists(marker),Is.True); Assert.That(model.Settings.SteamConnectionRemoved,Is.True);
            Assert.That(applied,Is.EqualTo(1)); Assert.That(model.SteamCard.Status,Is.EqualTo("Observation disabled"));
            Assert.That(model.SteamCard.Description,Does.Contain("preserved")); Assert.That(model.SteamCard.ShowSetup,Is.True);
        }
    }
}
