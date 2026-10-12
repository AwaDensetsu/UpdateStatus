using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers.Epic;
using PlayniteUpdateStatus.Launchers;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class ConnectionSetupTests
    {
        private string directory, state;
        [SetUp]
        public void CreateFixture()
        {
            directory = Path.Combine(Path.GetTempPath(), "UpdateStatusSetup-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            state = Path.Combine(directory, "setup.json");
        }
        [TearDown]
        public void DeleteFixture() { Directory.Delete(directory, true); }
        private EpicConnectionSetup Setup(Dictionary<string, string> values, Action<EpicStartupChange, string> write = null)
        {
            return new EpicConnectionSetup(state, (root, port) => values.Select(pair => new EpicStartupChange {
                Kind = "fixture", Location = pair.Key, Before = pair.Value,
                After = (pair.Value ?? "") + " " + EpicConnectionSetup.Arguments(port) }).ToList(),
                change => values[change.Location], write ?? ((change, value) => values[change.Location] = value));
        }
        [Test]
        public void SetupStoresBackupBeforeMutationAndRemovalRestoresExactValues()
        {
            var values = new Dictionary<string, string> { ["shortcut"] = "-silent Ω", ["protocol"] = null, ["startup"] = "-silent" };
            var original = new Dictionary<string, string>(values);
            var setup = Setup(values, (change, value) => {
                Assert.That(File.Exists(state), Is.True);
                values[change.Location] = value;
            });
            setup.Enable(directory);
            Assert.That(setup.Configured(directory), Is.True);
            var saved = File.ReadAllText(state);
            setup.Enable(directory);
            Assert.That(File.ReadAllText(state), Is.EqualTo(saved));
            setup.Remove();
            Assert.That(values, Is.EquivalentTo(original));
            Assert.That(setup.HasBackup, Is.False);
        }
        [Test]
        public void RemovalDoesNotOverwriteAUserEditAndStillRestoresOtherRoutes()
        {
            var values = new Dictionary<string, string> { ["shortcut"] = "", ["protocol"] = null };
            var setup = Setup(values);
            setup.Enable(directory);
            values["shortcut"] = "user edited this later";
            Assert.Throws<IOException>(() => setup.Remove());
            Assert.That(values["shortcut"], Is.EqualTo("user edited this later"));
            Assert.That(values["protocol"], Is.Null);
            Assert.That(setup.HasBackup, Is.True);
            values["shortcut"] = setup.Load().Changes[0].After;
            setup.Remove();
            Assert.That(values["shortcut"], Is.Empty);
            Assert.That(setup.HasBackup, Is.False);
        }
        private EpicConnectionSetup ProtocolSetup(Dictionary<string, string> values, string command, bool existed,
            Action<EpicStartupChange, string> write = null)
        {
            return new EpicConnectionSetup(state, (root, port) => new List<EpicStartupChange> {
                new EpicStartupChange { Kind = "protocol", Location = "protocol", Before = existed ? command : null,
                    Target = command, After = command + " " + EpicConnectionSetup.Arguments(port) },
                new EpicStartupChange { Kind = "fixture", Location = "shortcut", Before = "-silent",
                    After = "-silent " + EpicConnectionSetup.Arguments(port) }
            }, change => values[change.Location], write ?? ((change, value) => values[change.Location] = value));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void RemovalAcceptsTheExactOriginalProtocolCommandWrittenBackByTheLauncher(bool existed)
        {
            string command = "\"" + Path.Combine(directory, "EpicGamesLauncher.exe") + "\" %1";
            var values = new Dictionary<string, string> { ["protocol"] = existed ? command : null, ["shortcut"] = "-silent" };
            int protocolWrites = 0;
            var setup = ProtocolSetup(values, command, existed, (change, value) => {
                if (change.Kind == "protocol") protocolWrites++;
                values[change.Location] = value;
            });
            setup.Enable(directory);
            // Epic rewrites its clean command during startup, even when the
            // per-user override did not exist before our initial setup.
            values["protocol"] = command;
            protocolWrites = 0;
            setup.Remove();
            Assert.That(setup.HasBackup, Is.False);
            Assert.That(values["protocol"], Is.EqualTo(command));
            Assert.That(values["shortcut"], Is.EqualTo("-silent"));
            Assert.That(protocolWrites, Is.Zero);
        }
        [Test]
        public void ExplicitSetupCanReuseItsBackupAfterTheLauncherRestoresTheCleanProtocol()
        {
            string command = "\"" + Path.Combine(directory, "EpicGamesLauncher.exe") + "\" %1";
            var values = new Dictionary<string, string> { ["protocol"] = null, ["shortcut"] = "-silent" };
            var setup = ProtocolSetup(values, command, false);
            setup.Enable(directory);
            string originalBackup = File.ReadAllText(state);
            values["protocol"] = command;
            setup.Enable(directory);
            Assert.That(setup.Configured(directory), Is.True);
            Assert.That(File.ReadAllText(state), Is.EqualTo(originalBackup));
            setup.Remove();
            Assert.That(values["protocol"], Is.Null);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedRestartAcceptsOnlyTheExactCleanProtocolAndDoesNotRewriteSetup(bool existed)
        {
            string command = "\"" + Path.Combine(directory, "EpicGamesLauncher.exe") + "\" %1";
            var values = new Dictionary<string, string> { ["protocol"] = existed ? command : null, ["shortcut"] = "-silent" };
            int writes = 0;
            var setup = ProtocolSetup(values, command, existed, (change, value) => { writes++; values[change.Location] = value; });
            setup.Enable(directory);
            string saved = File.ReadAllText(state);
            string arguments = EpicConnectionSetup.Arguments(setup.Load().Port);
            Assert.That(setup.Configured(directory), Is.True);
            Assert.That(setup.GetRestartArguments(directory), Is.EqualTo(arguments));
            writes = 0;
            for (int restart = 0; restart < 3; restart++)
            {
                values["protocol"] = command;
                Assert.That(setup.Configured(directory), Is.False);
                Assert.That(setup.GetRestartArguments(directory), Is.EqualTo(arguments));
                Assert.That(values["protocol"], Is.EqualTo(command));
                Assert.That(File.ReadAllText(state), Is.EqualTo(saved));
            }
            Assert.That(writes, Is.Zero);
        }
        [TestCase(null)]
        [TestCase("extra arguments")]
        [TestCase("another installation")]
        public void RestartRejectsADeletedOrActuallyModifiedProtocolWithoutWriting(string edit)
        {
            string command = "\"" + Path.Combine(directory, "EpicGamesLauncher.exe") + "\" %1";
            var values = new Dictionary<string, string> { ["protocol"] = null, ["shortcut"] = "-silent" };
            int writes = 0;
            var setup = ProtocolSetup(values, command, false, (change, value) => { writes++; values[change.Location] = value; });
            setup.Enable(directory);
            string saved = File.ReadAllText(state);
            values["protocol"] = edit == null ? null : edit == "extra arguments" ? command + " -silent" : "\"C:\\Other\\EpicGamesLauncher.exe\" %1";
            var before = new Dictionary<string, string>(values);
            writes = 0;
            Assert.Throws<IOException>(() => setup.GetRestartArguments(directory));
            Assert.That(values, Is.EquivalentTo(before));
            Assert.That(File.ReadAllText(state), Is.EqualTo(saved));
            Assert.That(writes, Is.Zero);
        }
        [TestCase("shortcut")]
        [TestCase("startup")]
        public void CleanProtocolCannotAuthorizeRestartWithAnotherRestoredRoute(string kind)
        {
            string command = "\"" + Path.Combine(directory, "EpicGamesLauncher.exe") + "\" %1";
            var values = new Dictionary<string, string> { ["protocol"] = null, ["shortcut"] = "-silent" };
            var setup = ProtocolSetup(values, command, false);
            setup.Enable(directory);
            var record = setup.Load();
            record.Changes.Single(change => change.Location == "shortcut").Kind = kind;
            File.WriteAllText(state, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(record));
            values["protocol"] = command;
            values["shortcut"] = "-silent";
            Assert.Throws<IOException>(() => setup.GetRestartArguments(directory));
            Assert.That(setup.HasBackup, Is.True);
            Assert.That(values["shortcut"], Is.EqualTo("-silent"));
        }
        [Test]
        public void RestartRejectsAnotherInstallationBeforeReadingItsRoutes()
        {
            var setup = Setup(new Dictionary<string, string> { ["route"] = "before" });
            setup.Enable(directory);
            string saved = File.ReadAllText(state);
            var guarded = new EpicConnectionSetup(state, (root, port) => throw new AssertionException("No discovery"),
                change => throw new AssertionException("No route reads for another installation"),
                (change, value) => throw new AssertionException("No mutation"));
            Assert.Throws<IOException>(() => guarded.GetRestartArguments(Path.Combine(directory, "another")));
            Assert.That(File.ReadAllText(state), Is.EqualTo(saved));
        }
        [Test]
        public void UnreadableStartupRoutePreventsRestartRatherThanReturningConnectionArguments()
        {
            var setup = Setup(new Dictionary<string, string> { ["route"] = "before" });
            setup.Enable(directory);
            var guarded = new EpicConnectionSetup(state, (root, port) => throw new AssertionException("No discovery"),
                change => throw new IOException("Route unavailable"), (change, value) => throw new AssertionException("No mutation"));
            var error = Assert.Throws<IOException>(() => guarded.GetRestartArguments(directory));
            Assert.That(error.Message, Is.EqualTo("Route unavailable"));
            Assert.That(guarded.HasBackup, Is.True);
        }
        [TestCase(" -silent")]
        [TestCase(" -cefdebug=12345")]
        [TestCase(" --remote-debugging-address=0.0.0.0")]
        public void ChangedProtocolCommandsRemainProtectedEvenWhenTheyUseTheSameLauncher(string added)
        {
            string command = "\"" + Path.Combine(directory, "EpicGamesLauncher.exe") + "\" %1";
            var values = new Dictionary<string, string> { ["protocol"] = null, ["shortcut"] = "-silent" };
            var setup = ProtocolSetup(values, command, false);
            setup.Enable(directory);
            values["protocol"] = command + added;
            Assert.Throws<IOException>(() => setup.Remove());
            Assert.That(setup.HasBackup, Is.True);
            Assert.That(values["protocol"], Is.EqualTo(command + added));
            Assert.That(values["shortcut"], Is.EqualTo("-silent"));
        }
        [TestCase("shortcut")]
        [TestCase("startup")]
        public void OriginalProtocolRecognitionCannotAuthorizeAnotherKindOfStartupRoute(string kind)
        {
            string value = "original target";
            var values = new Dictionary<string, string> { ["route"] = null };
            var setup = new EpicConnectionSetup(state, (root, port) => new List<EpicStartupChange> {
                new EpicStartupChange { Kind = kind, Location = "route", Before = null, Target = value,
                    After = value + " " + EpicConnectionSetup.Arguments(port) }
            }, change => values[change.Location], (change, current) => values[change.Location] = current);
            setup.Enable(directory);
            values["route"] = value;
            Assert.Throws<IOException>(() => setup.Remove());
            Assert.That(setup.HasBackup, Is.True);
            Assert.That(values["route"], Is.EqualTo(value));
        }
        [Test]
        public async Task RemoveThenRestartSucceedsWhenTheLauncherAlreadyRestoredItsProtocol()
        {
            string command = "\"" + Path.Combine(directory, "EpicGamesLauncher.exe") + "\" %1";
            var values = new Dictionary<string, string> { ["protocol"] = null, ["shortcut"] = "-silent" };
            var setup = ProtocolSetup(values, command, false);
            setup.Enable(directory);
            values["protocol"] = command;
            int restarts = 0, saves = 0;
            var settings = new Settings { EpicEnabled = true };
            var model = new SettingsViewModel(settings, () => null, findEpicFolder: _ => directory,
                save: _ => saves++, epicConfigured: setup.Configured, epicHasBackup: () => setup.HasBackup,
                configureEpic: (folder, remove) => { Assert.That(remove, Is.True); setup.Remove(); return Task.CompletedTask; },
                restartEpic: _ => { Assert.That(setup.HasBackup, Is.False); restarts++; return Task.CompletedTask; },
                confirmRestart: _ => true, launcherSession: _ => "initial");
            await model.RefreshEpicLocationAsync();
            await model.RemoveEpicConnectionAsync();
            await model.RestartEpicAsync();
            Assert.That(settings.EpicConnectionRemoved, Is.True);
            Assert.That(saves, Is.EqualTo(1));
            Assert.That(restarts, Is.EqualTo(1));
            Assert.That(model.EpicOperationMessage, Is.Null);
            Assert.That(model.EpicStatus, Is.EqualTo("Not connected"));
            Assert.That(values["protocol"], Is.EqualTo(command));
            Assert.That(setup.GetRestartArguments(directory), Is.Empty);
        }
        [Test]
        public void FailedSetupRollsBackWrittenRoutes()
        {
            var values = new Dictionary<string, string> { ["first"] = "before", ["second"] = null };
            var setup = Setup(values, (change, value) => {
                if (change.Location == "second" && value != null) throw new UnauthorizedAccessException();
                values[change.Location] = value;
            });
            Assert.Throws<UnauthorizedAccessException>(() => setup.Enable(directory));
            Assert.That(values["first"], Is.EqualTo("before"));
            Assert.That(values["second"], Is.Null);
            Assert.That(setup.HasBackup, Is.False);
        }
        [Test]
        public void InterruptedSetupCanResumeWithoutReplacingTheOriginalBackup()
        {
            var values = new Dictionary<string, string> { ["first"] = "before", ["second"] = null };
            var setup = Setup(values);
            setup.Enable(directory);
            var saved = File.ReadAllText(state);
            values["second"] = null;
            setup.Enable(directory);
            Assert.That(setup.Configured(directory), Is.True);
            Assert.That(File.ReadAllText(state), Is.EqualTo(saved));
            setup.Remove();
            Assert.That(values["first"], Is.EqualTo("before"));
            Assert.That(values["second"], Is.Null);
        }
        [Test]
        public void AnotherInstallationCannotReplaceAnExistingBackup()
        {
            var setup = Setup(new Dictionary<string, string> { ["route"] = "before" });
            setup.Enable(directory);
            var saved = File.ReadAllText(state);
            Assert.Throws<IOException>(() => setup.Enable(Path.Combine(directory, "other")));
            Assert.That(File.ReadAllText(state), Is.EqualTo(saved));
        }
        [Test]
        public void CorruptBackupIsRetainedWithoutApplyingChanges()
        {
            var values = new Dictionary<string, string> { ["route"] = "before" };
            var setup = Setup(values);
            File.WriteAllText(state, "null");
            Assert.Throws<IOException>(() => setup.Enable(directory));
            Assert.Throws<IOException>(() => setup.Remove());
            Assert.Throws<IOException>(() => setup.GetRestartArguments(directory));
            Assert.That(values["route"], Is.EqualTo("before"));
            Assert.That(File.Exists(state), Is.True);
        }
        [TestCase(0)]
        [TestCase(1023)]
        [TestCase(65536)]
        public void SetupRejectsInvalidDebugPorts(int port)
        { Assert.Throws<ArgumentOutOfRangeException>(() => EpicConnectionSetup.Arguments(port)); }
        [Test]
        public void OversizedBackupIsRejectedBeforeChangingStartupValues()
        {
            var values = new Dictionary<string, string> { ["route"] = new string('x', 550000) };
            var setup = Setup(values);
            Assert.Throws<IOException>(() => setup.Enable(directory));
            Assert.That(values["route"].Length, Is.EqualTo(550000));
            Assert.That(setup.HasBackup, Is.False);
        }
        [TestCase("shortcut")]
        [TestCase("protocol")]
        [TestCase("startup")]
        public void NativeBackupValidationRejectsUnrelatedLocationsBeforeAccessingThem(string kind)
        {
            var record = new EpicSetupRecord { Root = directory, Port = 49152,
                Changes = new List<EpicStartupChange> { new EpicStartupChange {
                    Kind = kind, Location = @"C:\unrelated.lnk", Name = "unrelated", Target = @"C:\notepad.exe",
                    Before = "keep", After = "replace", ValueKind = 1 } } };
            File.WriteAllText(state, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(record));
            var setup = new EpicConnectionSetup(state);
            Assert.Throws<IOException>(() => setup.Remove());
            Assert.That(setup.HasBackup, Is.True);
        }
        [Test]
        public void NoStartupRoutesLeavesNoSetupBackup()
        {
            var setup = Setup(new Dictionary<string, string>());
            Assert.Throws<IOException>(() => setup.Enable(directory));
            Assert.That(setup.HasBackup, Is.False);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void SteamRemovalPreservesUnexpectedMarkerContents(bool folder)
        {
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "never execute");
            File.WriteAllText(Path.Combine(directory, "steamclient64.dll"), "never load");
            string marker = Path.Combine(directory, ".cef-enable-remote-debugging");
            if (folder)
            {
                Directory.CreateDirectory(marker);
                File.WriteAllText(Path.Combine(marker, "keep.txt"), "keep");
            }
            else File.WriteAllText(marker, "keep");
            Assert.DoesNotThrow(() => SteamUiAccess.Disable(directory));
            Assert.That(folder ? File.ReadAllText(Path.Combine(marker, "keep.txt")) : File.ReadAllText(marker), Is.EqualTo("keep"));
        }
        [Test]
        public void SteamRemovalDeletesOnlyTheEmptyMarkerAndIsIdempotent()
        {
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "never execute");
            File.WriteAllText(Path.Combine(directory, "steamclient64.dll"), "never load");
            new SteamConnectionSetup(directory).Enable();
            SteamUiAccess.Disable(directory);
            SteamUiAccess.Disable(directory);
            Assert.That(SteamUiAccess.Configured(directory), Is.False);
            Assert.That(File.Exists(Path.Combine(directory, "steam.exe")), Is.True);
        }
        [Test]
        public async Task EpicSetupIsOneTimeAndRemovalRequiresRestartOfTheExistingSession()
        {
            bool configured = false, backup = false;
            int writes = 0;
            string session = "old";
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => configured, epicHasBackup: () => backup,
                configureEpic: (folder, remove) => { writes++; configured = backup = !remove; return Task.CompletedTask; },
                launcherSession: _ => session);
            model.EpicConnection = () => new EpicSnapshot(DateTime.UtcNow, new EpicProduct[0], true, "Connected");
            await model.RefreshEpicLocationAsync();
            Assert.That(model.EpicCanConnect, Is.True);
            await model.ConnectEpicAsync();
            Assert.That(model.EpicStatus, Is.EqualTo("Restart required"));
            await model.ConnectEpicAsync();
            Assert.That(writes, Is.EqualTo(1));
            session = "new";
            model.RefreshEpicConnection();
            Assert.That(model.EpicStatus, Is.EqualTo("Connected"));
            await model.RemoveEpicConnectionAsync();
            Assert.That(model.EpicStatus, Is.EqualTo("Restart required"));
            session = "";
            model.RefreshEpicConnection();
            Assert.That(model.EpicStatus, Is.EqualTo("Not connected"));
            Assert.That(model.EpicCanConnect, Is.True);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task RestartIsNeverInvokedWithoutExplicitConfirmation(bool epic)
        {
            int calls = 0;
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "never execute");
            File.WriteAllText(Path.Combine(directory, "steamclient64.dll"), "never load");
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => directory, findEpicFolder: _ => directory,
                epicConfigured: _ => false, epicHasBackup: () => false,
                restartSteam: _ => { calls++; return Task.CompletedTask; },
                restartEpic: _ => { calls++; return Task.CompletedTask; }, confirmRestart: _ => false);
            model.InitializeLocation();
            await model.RefreshEpicLocationAsync();
            await (epic ? model.RestartEpicAsync() : model.RestartSteamAsync());
            Assert.That(calls, Is.Zero);
        }
        [Test]
        public async Task FailedEpicRestartKeepsRestartRequiredAndDisplaysTheFailure()
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => true, epicHasBackup: () => true,
                restartEpic: _ => throw new IOException("Exit Epic manually."), confirmRestart: _ => true, launcherSession: _ => "running");
            await model.RefreshEpicLocationAsync();
            await model.RestartEpicAsync();
            Assert.That(model.EpicStatus, Is.EqualTo("Restart required"));
            Assert.That(model.EpicOperationMessage, Is.EqualTo("Exit Epic manually."));
            Assert.That(model.EpicCanRestart, Is.True);
        }
        [Test]
        public async Task DelayedSetupCannotAttachRestartOrErrorsToANewerFolder()
        {
            var completion = new TaskCompletionSource<bool>();
            var settings = new Settings { EpicEnabled = true, EpicFolder = directory };
            var model = new SettingsViewModel(settings, () => null, findEpicFolder: folder => folder,
                epicConfigured: _ => false, epicHasBackup: () => false,
                configureEpic: (folder, remove) => { Assert.That(folder, Is.EqualTo(directory)); return completion.Task; },
                launcherSession: _ => "old");
            await model.RefreshEpicLocationAsync();
            var pending = model.ConnectEpicAsync();
            model.EpicFolderInput = Path.Combine(directory, "other");
            await model.RefreshEpicLocationAsync();
            completion.SetException(new IOException("failure for old folder"));
            await pending;
            Assert.That(model.EpicStatus, Is.EqualTo("Not connected"));
            Assert.That(model.EpicOperationMessage, Is.Null);
            Assert.That(model.EpicFolderInput, Does.EndWith("other"));
        }
        private static Task WaitForExit(Action close, Func<bool> running, Func<Task> pause, int attempts)
        {
            Func<Task<System.ComponentModel.Win32Exception>> begin = () => {
                try { close(); return Task.FromResult<System.ComponentModel.Win32Exception>(null); }
                catch (System.ComponentModel.Win32Exception error) { return Task.FromResult(error); }
            };
            return WaitForAsyncExit(begin, running, pause, attempts);
        }
        private static Task WaitForAsyncExit(Func<Task<System.ComponentModel.Win32Exception>> close,
            Func<bool> running, Func<Task> pause, int attempts)
        {
            return (Task)typeof(LauncherConnectionActions).GetMethod("WaitForMainExitAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { close, running, pause, attempts });
        }
        [Test]
        public void ABlockedNativeShutdownStillReachesTheManualExitDeadline()
        {
            var blocked = new TaskCompletionSource<System.ComponentModel.Win32Exception>();
            int pauses = 0;
            Assert.ThrowsAsync<LauncherStillRunningException>(async () =>
                await WaitForAsyncExit(() => blocked.Task, () => true,
                    () => { pauses++; return Task.CompletedTask; }, 3));
            Assert.That(pauses, Is.EqualTo(3));
            Assert.That(blocked.Task.IsCompleted, Is.False);
        }
        [Test]
        public async Task MainExitCanCompleteRestartEvenWhenTheNativeCallIsStillPending()
        {
            var blocked = new TaskCompletionSource<System.ComponentModel.Win32Exception>();
            await WaitForAsyncExit(() => blocked.Task, () => false,
                () => throw new AssertionException("An exited main must not keep waiting"), 1);
            Assert.That(blocked.Task.IsCompleted, Is.False);
        }
        [Test]
        public async Task ShutdownErrorDoesNotAbortReopeningOnceTheMainHasExited()
        {
            bool running = true;
            int pauses = 0;
            await WaitForExit(() => throw new System.ComponentModel.Win32Exception(351), () => running,
                () => { pauses++; running = false; return Task.CompletedTask; }, 2);
            Assert.That(pauses, Is.EqualTo(1));
        }
        [Test]
        public void ALauncherThatRemainsRunningRequiresManualExit()
        {
            Assert.ThrowsAsync<LauncherStillRunningException>(async () =>
                await WaitForExit(() => throw new System.ComponentModel.Win32Exception(351), () => true,
                    () => Task.CompletedTask, 2));
        }
        [Test]
        public async Task AnAlreadyExitedMainDoesNotWaitForOtherProcesses()
        {
            await WaitForExit(() => { }, () => false,
                () => throw new AssertionException("Must not wait for a browser helper"), 2);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task SteamManualExitFallbackReopensOnlyAfterConfirmation(bool confirmed)
        {
            int opens = 0;
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(directory, "steamclient64.dll"), "fixture; never load");
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => directory,
                restartSteam: _ => throw new LauncherStillRunningException(), confirmRestart: _ => true,
                confirmManualExit: _ => confirmed,
                openAfterExit: (folder, epic) => { Assert.That(epic, Is.False); opens++; return Task.CompletedTask; });
            model.InitializeLocation();
            await model.RestartSteamAsync();
            Assert.That(opens, Is.EqualTo(confirmed ? 1 : 0));
            if (!confirmed) Assert.That(model.ConnectionStatus, Does.Contain("exit Steam completely"));
        }
        [Test]
        public async Task EpicRestartFailureDoesNotRequestManualExitOrColdOpen()
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => true, epicHasBackup: () => true,
                restartEpic: _ => throw new LauncherStillRunningException(), confirmRestart: _ => true,
                confirmManualExit: _ => throw new AssertionException("Epic must report its automatic restart failure"),
                openAfterExit: (folder, epic) => throw new AssertionException("Must not reopen before exit"));
            await model.RefreshEpicLocationAsync();
            await model.RestartEpicAsync();
            Assert.That(model.EpicOperationMessage, Does.Contain("still running"));
            Assert.That(model.EpicCanRestart, Is.True);
        }
        private static Task CloseEpic(Action close, Action terminate, Func<bool> running,
            Func<Task> pause, int attempts = 2)
        {
            return (Task)typeof(LauncherConnectionActions).GetMethod("CloseEpicAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { close, terminate, running, pause, attempts });
        }
        [Test]
        public async Task AlreadyExitedEpicRequiresNoCloseOrTermination()
        {
            await CloseEpic(() => throw new AssertionException("Already exited"),
                () => throw new AssertionException("Already exited"), () => false,
                () => throw new AssertionException("Must not wait"));
        }
        [Test]
        public async Task NormalEpicExitNeverUsesTermination()
        {
            bool running = true;
            await CloseEpic(() => running = false, () => throw new AssertionException("Closed normally"),
                () => running, () => throw new AssertionException("Must not wait"));
        }
        [Test]
        public async Task EpicIgnoringCloseIsTerminatedOnceAfterTheGracePeriod()
        {
            bool running = true;
            int pauses = 0, closes = 0, kills = 0;
            await CloseEpic(() => closes++, () => { Assert.That(pauses, Is.EqualTo(2)); kills++; running = false; },
                () => running, () => { pauses++; return Task.CompletedTask; });
            Assert.That(closes, Is.EqualTo(1));
            Assert.That(kills, Is.EqualTo(1));
        }
        [Test]
        public async Task NaturalEpicExitAtTheGraceDeadlineDoesNotTerminate()
        {
            bool running = true;
            int pauses = 0;
            await CloseEpic(() => { }, () => throw new AssertionException("Exited during grace period"),
                () => running, () => { if (++pauses == 2) running = false; return Task.CompletedTask; });
        }
        [Test]
        public void EpicStillRunningAfterTerminationReachesABoundedFailure()
        {
            int pauses = 0, kills = 0;
            Assert.ThrowsAsync<IOException>(async () => await CloseEpic(() => { }, () => kills++, () => true,
                () => { pauses++; return Task.CompletedTask; }));
            Assert.That(kills, Is.EqualTo(1));
            Assert.That(pauses, Is.EqualTo(4));
        }
        [Test]
        public void EpicTerminationDeniedPreservesTheErrorAndDoesNotKeepWaiting()
        {
            int pauses = 0;
            var failure = new System.ComponentModel.Win32Exception(5);
            var actual = Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(async () =>
                await CloseEpic(() => { }, () => throw failure, () => true,
                    () => { pauses++; return Task.CompletedTask; }));
            Assert.That(actual, Is.SameAs(failure));
            Assert.That(pauses, Is.EqualTo(2));
        }
        [Test]
        public async Task EpicExitingBetweenCheckAndTerminationCompletesSuccessfully()
        {
            bool running = true;
            await CloseEpic(() => { }, () => { running = false; throw new InvalidOperationException(); },
                () => running, () => Task.CompletedTask);
        }
        [Test]
        public void InvalidEpicTerminationWhileStillAliveDoesNotAllowReopening()
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () => await CloseEpic(() => { },
                () => throw new InvalidOperationException(), () => true, () => Task.CompletedTask));
        }
        [Test]
        public async Task EpicExitingDuringTheCloseRequestDoesNotUseTermination()
        {
            bool running = true;
            await CloseEpic(() => { running = false; throw new InvalidOperationException(); },
                () => throw new AssertionException("Already exited"), () => running, () => Task.CompletedTask);
        }
        [Test]
        public async Task SuccessfulSteamRemovalReportsCompletionSeparatelyFromRestart()
        {
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(directory, "steamclient64.dll"), "fixture; never load");
            SteamUiAccess.Enable(directory);
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => directory,
                disableAccess: folder => { File.Delete(Path.Combine(folder,".cef-enable-remote-debugging")); return Task.CompletedTask; },
                launcherSession: _ => "still running");
            model.InitializeLocation();
            await model.RemoveSteamConnectionAsync();
            Assert.That(model.SteamRemovalNotice, Is.EqualTo("Connection removed."));
            Assert.That(model.ConnectionStatus, Is.EqualTo("Restart required"));
            Assert.That(SteamUiAccess.Configured(directory), Is.False);
        }
        [Test]
        public async Task ReopenedEpicWaitsForARealObservationBeforeClaimingConnected()
        {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => true, epicHasBackup: () => true,
                restartEpic: _ => Task.CompletedTask, confirmRestart: _ => true, launcherSession: _ => "running");
            var snapshot = new EpicSnapshot(DateTime.MinValue, new EpicProduct[0], false, "Not connected");
            model.EpicConnection = () => snapshot;
            await model.RefreshEpicLocationAsync();
            await model.RestartEpicAsync();
            Assert.That(model.EpicStatus, Is.EqualTo("Connecting…"));
            snapshot = new EpicSnapshot(DateTime.UtcNow, new EpicProduct[0], true, "Connected");
            model.RefreshEpicConnection();
            Assert.That(model.EpicStatus, Is.EqualTo("Connected"));
        }
        [Test]
        public async Task BusySetupRejectsRepeatedClicksAndRestart()
        {
            int calls = 0;
            var completion = new TaskCompletionSource<bool>();
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => directory,
                epicConfigured: _ => false, epicHasBackup: () => false,
                configureEpic: (folder, remove) => { calls++; return completion.Task; },
                restartEpic: _ => throw new AssertionException("Restart must wait for setup"), confirmRestart: _ => true,
                launcherSession: _ => "");
            await model.RefreshEpicLocationAsync();
            var pending = model.ConnectEpicAsync();
            try
            {
                await model.ConnectEpicAsync();
                await model.RestartEpicAsync();
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(model.EpicCanConnect, Is.False);
            }
            finally { completion.SetResult(true); }
            await pending;
        }
    }
}
