using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("Settings")]
    public sealed class SettingsEditingTests
    {
        [TestCase(499)]
        [TestCase(2001)]
        public void RejectsInvalidPollingIntervals(int interval)
        {
            var model = new SettingsViewModel(new Settings { PollingInterval = interval });
            List<string> errors;
            Assert.That(model.VerifySettings(out errors), Is.False);
            Assert.That(errors.Single(), Does.Contain("Polling interval"));
        }

        [Test]
        public void MissingOrInaccessibleDiscoveryLeavesBlankInputAndDoesNotEnableAccess()
        {
            int discoveries = 0, activations = 0;
            var settings = new Settings();
            var model = new SettingsViewModel(settings, () => {
                discoveries++;
                throw new UnauthorizedAccessException();
            }, enableAccess: _ => { activations++; return Task.CompletedTask; });
            Assert.That(discoveries, Is.Zero);
            model.InitializeLocation();
            Assert.That(model.FolderInput, Is.Empty);
            Assert.That(settings.SteamFolder, Is.Empty);
            Assert.That(model.FolderStatus, Is.EqualTo("Steam not found automatically"));
            Assert.That(model.CanEnableAccess, Is.False);
            model.EnableLocalAccessAsync().GetAwaiter().GetResult();
            Assert.That(activations, Is.Zero);
        }

        [Test]
        public async Task ExplicitSetupPreventsDuplicateRequestsAndKeepsCancellationVisible()
        {
            string root = Fixture();
            try
            {
                int activations = 0;
                var completion = new TaskCompletionSource<bool>();
                var model = new SettingsViewModel(new Settings(), () => root,
                    enableAccess: _ => { activations++; return completion.Task; });
                model.InitializeLocation();
                Assert.That(File.Exists(Path.Combine(root, ".cef-enable-remote-debugging")), Is.False);
                Assert.That(model.Settings.SteamFolder, Is.Empty);
                var first = model.EnableLocalAccessAsync();
                await model.EnableLocalAccessAsync();
                Assert.That(activations, Is.EqualTo(1));
                Assert.That(model.CanEnableAccess, Is.False);
                completion.SetException(new System.ComponentModel.Win32Exception(1223));
                await first;
                Assert.That(model.CanEnableAccess, Is.True);
                model.RefreshSteamLocation();
                Assert.That(model.ConnectionStatus, Does.Contain("cancelled"));
            }
            finally { Directory.Delete(Path.GetDirectoryName(root), true); }
        }

        [Test]
        public void BrowseCommitsManualChoiceEvenWhenItMatchesDetectedText()
        {
            string root = Fixture();
            try
            {
                var settings = new Settings();
                var model = new SettingsViewModel(settings, () => root, browse: () => root);
                model.InitializeLocation();
                Assert.That(model.FolderInput, Is.EqualTo(root));
                Assert.That(settings.SteamFolder, Is.Empty);
                model.BrowseSteamFolder();
                Assert.That(settings.SteamFolder, Is.EqualTo(root));
                Assert.That(model.FolderStatus, Is.EqualTo("Manual Steam folder selected"));
            }
            finally { Directory.Delete(Path.GetDirectoryName(root), true); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task SetupReplyCannotAttachItsFailureToAChangedFolder(bool cancelled, bool returnToOriginal)
        {
            string first = Fixture(), second = Fixture();
            try
            {
                var completion = new TaskCompletionSource<bool>();
                var model = new SettingsViewModel(new Settings(), () => first, enableAccess: _ => completion.Task);
                model.InitializeLocation();
                var pending = model.EnableLocalAccessAsync();
                model.FolderInput = second;
                if (returnToOriginal) model.FolderInput = first;
                completion.SetException(cancelled ? (Exception)new System.ComponentModel.Win32Exception(1223) : new IOException("old folder failed"));
                await pending;
                Assert.That(model.ConnectionStatus, Is.EqualTo("Not connected"));
                Assert.That(model.CanEnableAccess, Is.True);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(first), true);
                Directory.Delete(Path.GetDirectoryName(second), true);
            }
        }

        internal static string Fixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "Update Status setup " + Guid.NewGuid(), "Steam Ω");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "steam.exe"), "fixture only; never execute");
            File.WriteAllText(Path.Combine(root, "steamclient64.dll"), "fixture only; never load");
            return root;
        }
    }

    [TestFixture]
    [Category("FirstInstallation")]
    public sealed class LocalAccessSetupTests
    {
        private static void Enable(string root, Action<string> write, Action<string> elevate)
        {
            typeof(SteamLocalAccessSetup).GetMethod("Enable", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { root, write, elevate });
        }

        [Test]
        public void WritableInstallationCreatesOnlyMarkerWithoutElevation()
        {
            string root = SettingsEditingTests.Fixture();
            try
            {
                int elevations = 0;
                Enable(root, SteamUiAccess.Enable, _ => elevations++);
                Assert.That(SteamUiAccess.Configured(root), Is.True);
                Assert.That(new FileInfo(Path.Combine(root, ".cef-enable-remote-debugging")).Length, Is.Zero);
                Assert.That(elevations, Is.Zero);
            }
            finally { Directory.Delete(Path.GetDirectoryName(root), true); }
        }

        [Test]
        public void AccessDeniedRequestsElevationOnceForNormalizedSelectedFolder()
        {
            string root = SettingsEditingTests.Fixture();
            try
            {
                int writes = 0, elevations = 0;
                Enable(root + Path.DirectorySeparatorChar, _ => { writes++; throw new UnauthorizedAccessException(); }, selected => {
                    Assert.That(selected, Is.EqualTo(root));
                    elevations++;
                    SteamUiAccess.Enable(selected);
                });
                Assert.That(writes, Is.EqualTo(1));
                Assert.That(elevations, Is.EqualTo(1));
                Assert.That(SteamUiAccess.Configured(root), Is.True);
            }
            finally { Directory.Delete(Path.GetDirectoryName(root), true); }
        }

        [Test]
        public void ExistingMarkerIsPreservedWithoutWriteOrElevation()
        {
            string root = SettingsEditingTests.Fixture();
            try
            {
                string marker = Path.Combine(root, ".cef-enable-remote-debugging");
                File.WriteAllText(marker, "keep existing marker");
                Enable(root, _ => Assert.Fail("must not write"), _ => Assert.Fail("must not elevate"));
                Assert.That(File.ReadAllText(marker), Is.EqualTo("keep existing marker"));
            }
            finally { Directory.Delete(Path.GetDirectoryName(root), true); }
        }

        [Test]
        public void InvalidFolderIsRejectedBeforeAnyWriteOrElevation()
        {
            var error = Assert.Throws<TargetInvocationException>(() => Enable("relative-folder",
                _ => Assert.Fail("must not write"), _ => Assert.Fail("must not elevate")));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void OrdinaryIoFailureDoesNotTriggerElevation()
        {
            string root = SettingsEditingTests.Fixture();
            try
            {
                var error = Assert.Throws<TargetInvocationException>(() => Enable(root,
                    _ => { throw new IOException("device unavailable"); }, _ => Assert.Fail("must not elevate")));
                Assert.That(error.InnerException, Is.TypeOf<IOException>());
            }
            finally { Directory.Delete(Path.GetDirectoryName(root), true); }
        }
    }
}
