using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("Settings")]
    public sealed class EpicSettingsTests
    {
        [Test]
        public async Task AutomaticPathIsDisplayedWithoutBecomingAManualOverride()
        {
            int calls = 0;
            var settings = new Settings { EpicEnabled = true };
            var model = new SettingsViewModel(settings, () => null,
                findEpicFolder: folder => { calls++; Assert.That(folder, Is.Null); return @"C:\Epic"; },
                epicConfigured: _ => false, epicHasBackup: () => false);
            Assert.That(calls, Is.Zero);
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicFolderInput, Is.EqualTo(@"C:\Epic"));
            Assert.That(settings.EpicFolder, Is.Empty);
            Assert.That(model.EpicFolderStatus, Is.EqualTo("Epic found automatically"));
            Assert.That(model.EpicStatus, Is.EqualTo("Not connected"));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task MissingOrInaccessibleAutomaticPathStaysEmpty(bool inaccessible)
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null,
                findEpicFolder: _ => { if (inaccessible) throw new UnauthorizedAccessException(); return null; });
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicFolderInput, Is.Empty);
            Assert.That(model.EpicFolderStatus, Is.EqualTo("Epic not found automatically"));
            Assert.That(model.EpicCanConnect, Is.False);
        }
        [Test]
        public async Task ExplicitManualInputIsPreservedAndValidatedIndependentlyOfDetection()
        {
            var settings = new Settings { EpicEnabled = true, EpicFolder = @"D:\Epic" };
            var model = new SettingsViewModel(settings, () => null, findEpicFolder: folder => {
                Assert.That(folder, Is.EqualTo(@"D:\Epic")); return @"D:\Epic\Launcher";
            });
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicFolderInput, Is.EqualTo(settings.EpicFolder));
            Assert.That(model.EpicFolderStatus, Is.EqualTo("Manual Epic folder selected"));
        }
        [Test]
        public async Task ClearingManualInputRestoresAutomaticDisplay()
        {
            var settings = new Settings { EpicEnabled = true, EpicFolder = @"D:\Epic" };
            var model = new SettingsViewModel(settings, () => null, findEpicFolder: folder => folder ?? @"C:\Epic");
            await model.RefreshEpicLocationAsync();
            model.EpicFolderInput = "";
            await model.RefreshEpicLocationAsync();
            Assert.That(settings.EpicFolder, Is.Empty);
            Assert.That(model.EpicFolderInput, Is.EqualTo(@"C:\Epic"));
            Assert.That(model.EpicFolderStatus, Is.EqualTo("Epic found automatically"));
        }
        [Test]
        public async Task BrowseMakesAManualChoiceEvenWhenItMatchesTheDetectedPath()
        {
            var settings = new Settings { EpicEnabled = true };
            var model = new SettingsViewModel(settings, () => null, browse: () => @"C:\Epic", findEpicFolder: _ => @"C:\Epic");
            await model.RefreshEpicLocationAsync();
            model.BrowseEpicFolder();
            await model.RefreshEpicLocationAsync();
            Assert.That(settings.EpicFolder, Is.EqualTo(@"C:\Epic"));
            Assert.That(model.EpicFolderStatus, Is.EqualTo("Manual Epic folder selected"));
        }
        [Test]
        public async Task LateAutomaticDiscoveryCannotOverwriteANewerManualChoice()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var settings = new Settings { EpicEnabled = true };
                var model = new SettingsViewModel(settings, () => null, findEpicFolder: folder => {
                    if (folder == null) { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); return @"C:\Old"; }
                    return folder;
                });
                var displayed = new List<string>();
                model.PropertyChanged += (s,e) => { if (e.PropertyName == nameof(model.EpicFolderInput)) displayed.Add(model.EpicFolderInput); };
                var pending = model.RefreshEpicLocationAsync();
                try
                {
                    Assert.That(entered.Wait(2000), Is.True);
                    model.EpicFolderInput = @"D:\New";
                }
                finally { release.Set(); }
                await pending;
                Assert.That(model.EpicFolderInput, Is.EqualTo(@"D:\New"));
                Assert.That(settings.EpicFolder, Is.EqualTo(@"D:\New"));
                Assert.That(displayed, Does.Not.Contain(@"C:\Old"));
            }
        }
        [Test]
        public async Task RoundTripEditRejectsAnOldDiscoveryFailure()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int calls = 0;
                var settings = new Settings { EpicEnabled = true, EpicFolder = @"D:\Epic" };
                var model = new SettingsViewModel(settings, () => null, findEpicFolder: folder => {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        entered.Set(); if (!release.Wait(3000)) throw new TimeoutException();
                        throw new UnauthorizedAccessException();
                    }
                    return folder;
                });
                var pending = model.RefreshEpicLocationAsync();
                try
                {
                    Assert.That(entered.Wait(2000), Is.True);
                    model.EpicFolderInput = @"E:\Other";
                    model.EpicFolderInput = @"D:\Epic";
                }
                finally { release.Set(); }
                await pending;
                Assert.That(model.EpicFolderStatus, Is.EqualTo("Manual Epic folder selected"));
                Assert.That(model.EpicCanConnect, Is.True);
            }
        }
    }
}
