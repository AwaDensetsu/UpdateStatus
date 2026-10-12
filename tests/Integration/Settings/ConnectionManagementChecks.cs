using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Launchers;
using PlayniteUpdateStatus.Launchers.Epic;
using PlayniteUpdateStatus.UI;

internal static partial class Program
{
    private static string NativeProcessSession(string executable, bool readerAssembly = false)
    {
        var assembly = readerAssembly ? Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"SteamUpdateReader.exe")) : typeof(Plugin).Assembly;
        var type = assembly.GetType("PlayniteUpdateStatus.Launchers.Steam.SteamSessionDiscovery", true);
        var method = type.GetMethod("TryRead", BindingFlags.Static | BindingFlags.NonPublic);
        var arguments = new object[] { executable, null };
        Eq(true, (bool)method.Invoke(null, arguments));
        return (string)arguments[1];
    }

    private static void FreshSteamSnapshotMatchesManaged()
    {
        using (var current = Process.GetCurrentProcess())
        {
            string ownExecutable = Path.GetFileName(current.MainModule.FileName);
            Eq(true, NativeProcessSession(ownExecutable).Split(',').Contains(current.Id.ToString()));
            Eq(true, NativeProcessSession(ownExecutable, true).Split(',').Contains(current.Id.ToString()));
            var processes = Process.GetProcessesByName("steam");
            try
            {
                string expected = string.Join(",", processes.Where(p => p.SessionId == current.SessionId).Select(p => p.Id).OrderBy(id => id));
                Eq(expected, NativeProcessSession("steam.exe"));
                Eq(expected, PlayniteUpdateStatus.Launchers.Steam.SteamLocator.SessionId());
                Eq(expected, NativeProcessSession("steam.exe", true));
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private static void FreshSnapshotFixtureLifecycle()
    {
        string directory = Path.Combine(Path.GetTempPath(),"Session-snapshot-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"UpdateStatus.RestartFixture.exe");
        string name = Path.GetFileName(executable);
        string before = NativeProcessSession(name);
        int? previous = null;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                string status = Path.Combine(directory,"status-" + pass + ".txt");
                using (var child = Process.Start(new ProcessStartInfo(executable,"\"" + status + "\"") {
                    UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    try
                    {
                        WaitForFixture(() => File.Exists(status));
                        string live = NativeProcessSession(name);
                        Eq(true, live.Split(',').Contains(child.Id.ToString()));
                        if (previous.HasValue) Eq(false, live.Split(',').Contains(previous.Value.ToString()));
                    }
                    finally { File.WriteAllText(status + ".stop","stop owned disposable fixture"); Eq(true, child.WaitForExit(5000)); }
                    Eq(before, NativeProcessSession(name));
                    previous = child.Id;
                }
            }
        }
        finally { Directory.Delete(directory,true); }
    }

    private static void FreshSnapshotRepeatedCleanup()
    {
        using (var current = Process.GetCurrentProcess())
        {
            string name = Path.GetFileName(current.MainModule.FileName);
            for (int call = 0; call < 5; call++) NativeProcessSession(name);
            GC.Collect(); GC.WaitForPendingFinalizers();
            current.Refresh(); int handles = current.HandleCount;
            for (int call = 0; call < 100; call++)
                Eq(true, NativeProcessSession(name).Split(',').Contains(current.Id.ToString()));
            GC.Collect(); GC.WaitForPendingFinalizers();
            current.Refresh(); Eq(true, current.HandleCount <= handles + 4);
        }
    }

    private static void ConnectionManagementChecks()
    {
        SharedConnectionCardChecks();
        Test("native Steam snapshot matches managed current-session inventory", FreshSteamSnapshotMatchesManaged);
        Test("native process snapshot immediately observes owned fixture exit and replacement", FreshSnapshotFixtureLifecycle);
        Test("native process snapshot releases its enumeration buffers", FreshSnapshotRepeatedCleanup);
        Test("Steam reader binds WMI ownership to a real process instance", () => {
            var assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"SteamUpdateReader.exe"));
            var transport = assembly.GetType("SteamCefTransport",true);
            var method = transport.GetMethod("ProcessUserSid",BindingFlags.Static|BindingFlags.NonPublic);
            using (var current = Process.GetCurrentProcess())
                Eq(System.Security.Principal.WindowsIdentity.GetCurrent().User.Value,(string)method.Invoke(null,new object[] { (uint)current.Id }));
        });
        Test("missing launcher installations have no setup or restart action", () => {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => null,
                epicConfigured: _ => false, epicHasBackup: () => false);
            var view = new SettingsView(model, () => null);
            var discovery = model.RefreshEpicLocationAsync();
            PumpFolderStatus(() => discovery.IsCompleted); discovery.GetAwaiter().GetResult();
            var window = new TestWindow { Content = view, Width = 620, Height = 740, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                var tabs = (TabControl)view.FindName("LauncherSettingsTabs");
                foreach (string launcher in new[] { "Steam","Epic" })
                {
                    tabs.SelectedIndex = launcher == "Steam" ? 1 : 2; window.UpdateLayout();
                    PumpFolderStatus(() => ((LauncherConnectionCard)view.FindName(launcher+"CardControl")).State != null);
                    var card = (LauncherConnectionCard)view.FindName(launcher+"CardControl");
                    Eq("Launcher not found",card.State.Status);
                    Eq(false,card.State.CanSetup); Eq(false,card.State.CanRelaunch);
                    Eq(Visibility.Collapsed,((Button)view.FindName(launcher+"ConnectButton")).Visibility);
                    Eq(Visibility.Collapsed,((Button)view.FindName(launcher+"RestartButton")).Visibility);
                    Eq(false,((Button)view.FindName(launcher+"RemoveButton")).IsEnabled);
                    Eq("Remove connection",((Button)view.FindName(launcher+"RemoveButton")).Content);
                }
            }
            finally { window.Close(); }
        });
        Test("both launcher settings render restart-required and removal without launching clients", () => {
            string fixture = Path.Combine(Path.GetTempPath(), "Connection-ui-" + Guid.NewGuid());
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(fixture, "steamclient64.dll"), "fixture; never load");
            File.WriteAllText(Path.Combine(fixture, ".cef-enable-remote-debugging"), "");
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => fixture, findEpicFolder: _ => @"C:\Program Files (x86)\Epic Games\Launcher",
                epicConfigured: _ => true, epicHasBackup: () => true, launcherSession: _ => "running");
            var view = new SettingsView(model, () => fixture);
            var discovery = model.RefreshEpicLocationAsync();
            PumpFolderStatus(() => discovery.IsCompleted);
            discovery.GetAwaiter().GetResult();
            BindSettingsView(view);
            view.Foreground = System.Windows.Media.Brushes.Black;
            var window = new TestWindow { Content = view, Width = 620, Height = 810, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25,29,38)),
                Foreground = System.Windows.Media.Brushes.White };
            try
            {
                window.Show();
                var tabs = (TabControl)view.FindName("LauncherSettingsTabs");
                tabs.SelectedIndex = 2; window.UpdateLayout();
                Eq("Restart required", model.EpicStatus);
                Eq(true, ((Button)view.FindName("EpicRemoveButton")).IsEnabled);
                Render(view, "connection-settings-epic-1.1.4.png");
                tabs.SelectedIndex = 1; window.UpdateLayout();
                Eq("Restart required", model.ConnectionStatus);
                Eq(true, ((Button)view.FindName("SteamRemoveButton")).IsEnabled);
                Render(view, "connection-settings-steam-1.1.4.png");
            }
            finally { window.Close(); Directory.Delete(fixture, true); }
        });
        Test("removal lives under connection details and follows connected, lost and restored readiness", () => {
            string fixture = Path.Combine(Path.GetTempPath(), "Removal-ui-" + Guid.NewGuid());
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture,"steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(fixture,"steamclient64.dll"), "fixture; never load");
            File.WriteAllText(Path.Combine(fixture,".cef-enable-remote-debugging"), "");
            try
            {
                var snapshot = new EpicSnapshot(DateTime.MinValue,new EpicProduct[0],false,"");
                int removeCalls = 0;
                var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => fixture,
                    findEpicFolder: _ => @"C:\Epic fixture", epicConfigured: _ => true, epicHasBackup: () => true, launcherSession: _ => "running",
                    configureEpic: (_, remove) => { removeCalls++; return System.Threading.Tasks.Task.CompletedTask; },
                    disableAccess: _ => { removeCalls++; return System.Threading.Tasks.Task.CompletedTask; });
                model.EpicConnection = () => snapshot;
                var view = new SettingsView(model, () => fixture);
                var discovery = model.RefreshEpicLocationAsync();
                PumpFolderStatus(() => discovery.IsCompleted); discovery.GetAwaiter().GetResult();
                BindSettingsView(view);
                var steam = (Button)view.FindName("SteamRemoveButton");
                var epic = (Button)view.FindName("EpicRemoveButton");
                foreach (string launcher in new[] { "Steam", "Epic" })
                {
                    var details = (Expander)view.FindName(launcher+"ConnectionDetails");
                    var panel = (StackPanel)details.Content;
                    Eq(panel, ((Button)view.FindName(launcher+"RemoveButton")).Parent);
                    Eq(view.FindName(launcher+"RemoveButton"),panel.Children[panel.Children.Count-1]);
                    var disable = (TextBlock)panel.Children[panel.Children.Count-2];
                    Eq(true,disable.Text.Contains("Remove connection"));
                    Eq(false,panel.Children.OfType<TextBlock>().Any(block => block.Text.Contains("Other local programs can also inspect and control")));
                }
                Eq(true,steam.IsEnabled); Eq(true,epic.IsEnabled);
                steam.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                epic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Eq(0,removeCalls);
                PlayniteUpdateStatus.Launchers.Steam.SteamUiAccess.ObserveConnection(fixture);
                snapshot = new EpicSnapshot(DateTime.UtcNow,new EpicProduct[0],true,"");
                model.RefreshSteamLocation(); model.RefreshEpicConnection();
                view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
                Eq("Connected",model.ConnectionStatus); Eq("Connected",model.EpicStatus);
                Eq(true,steam.IsEnabled); Eq(true,epic.IsEnabled);
                model.Settings.SteamConnectionRemoved = true;
                snapshot = new EpicSnapshot(DateTime.UtcNow.AddMinutes(-1),new EpicProduct[0],true,"");
                model.RefreshSteamLocation(); model.RefreshEpicConnection();
                view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
                Eq(false,steam.IsEnabled); Eq(true,epic.IsEnabled);
                model.Settings.SteamConnectionRemoved = false;
                PlayniteUpdateStatus.Launchers.Steam.SteamUiAccess.ObserveConnection(fixture);
                snapshot = new EpicSnapshot(DateTime.UtcNow,new EpicProduct[0],true,"");
                model.RefreshSteamLocation(); model.RefreshEpicConnection();
                view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
                Eq(true,steam.IsEnabled); Eq(true,epic.IsEnabled);
                model.Settings.EpicEnabled = false; model.RefreshEpicConnection();
                view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
                Eq(false,epic.IsEnabled);
            }
            finally { Directory.Delete(fixture,true); }
        });
        foreach (bool epicRestart in new[] { false, true })
            Test((epicRestart ? "Epic" : "Steam")+" restart disables all connection actions and rejects repeated requests", () => {
                string fixture = Path.Combine(Path.GetTempPath(),"Busy-connection-ui-"+Guid.NewGuid());
                Directory.CreateDirectory(fixture);
                File.WriteAllText(Path.Combine(fixture,"steam.exe"),"fixture; never execute");
                File.WriteAllText(Path.Combine(fixture,"steamclient64.dll"),"fixture; never load");
                try
                {
                    var complete = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    int restarts = 0, removals = 0, setups = 0;
                    Func<string,System.Threading.Tasks.Task> restart = _ => { restarts++; return complete.Task; };
                    var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => fixture,
                        enableAccess: _ => { setups++; return System.Threading.Tasks.Task.CompletedTask; },
                        findEpicFolder: _ => @"C:\Epic fixture", epicConfigured: _ => false, epicHasBackup: () => false,
                        configureEpic: (_,remove) => { if (remove) removals++; else setups++; return System.Threading.Tasks.Task.CompletedTask; },
                        disableAccess: _ => { removals++; return System.Threading.Tasks.Task.CompletedTask; },
                        restartSteam: restart, restartEpic: restart, launcherSession: _ => "", confirmRestart: _ => true);
                    var view = new SettingsView(model, () => fixture);
                    var discovery = model.RefreshEpicLocationAsync();
                    PumpFolderStatus(() => discovery.IsCompleted); discovery.GetAwaiter().GetResult();
                    BindSettingsView(view);
                    Eq("Set up connection",((Button)view.FindName("SteamConnectButton")).Content);
                    Eq("Set up connection",((Button)view.FindName("EpicConnectButton")).Content);
                    // Match a real WPF button event: async completion returns to the UI context.
                    var previousContext = System.Threading.SynchronizationContext.Current;
                    System.Threading.Tasks.Task pending;
                    try
                    {
                        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(view.Dispatcher));
                        pending = epicRestart ? model.RestartEpicAsync() : model.RestartSteamAsync();
                    }
                    finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); }
                    view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
                    foreach (string name in new[] { "SteamRestartButton","EpicRestartButton","SteamRemoveButton",
                        "EpicRemoveButton","SteamConnectButton","EpicConnectButton" })
                        Eq(false,((Button)view.FindName(name)).IsEnabled);
                    var repeated = System.Threading.Tasks.Task.WhenAll(model.RestartSteamAsync(),model.RestartEpicAsync(),
                        model.RemoveSteamConnectionAsync(),model.RemoveEpicConnectionAsync(),model.EnableLocalAccessAsync(),model.ConnectEpicAsync());
                    Eq(true,repeated.IsCompleted); repeated.GetAwaiter().GetResult();
                    Eq(1,restarts); Eq(0,removals); Eq(0,setups);
                    complete.SetResult(true);
                    PumpFolderStatus(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                    Eq(true,model.SteamCanRestart); Eq(true,model.EpicCanRestart);
                    view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
                    // Restart is hidden and unavailable until setup exists; Setup is relevant here.
                    Eq(false,((Button)view.FindName("SteamRestartButton")).IsEnabled);
                    Eq(false,((Button)view.FindName("EpicRestartButton")).IsEnabled);
                    Eq(true,((Button)view.FindName("SteamConnectButton")).IsEnabled);
                    Eq(true,((Button)view.FindName("EpicConnectButton")).IsEnabled);
                }
                finally { Directory.Delete(fixture,true); }
            });
        Test("Windows setup preserves Greek, Chinese, Japanese, Arabic, Cyrillic and emoji in shortcuts", () => {
            string fixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "UpdateStatus-test-" + Guid.NewGuid());
            string epicRoot = Path.Combine(fixture, "Launcher Ω 中文 日本語 العربية Русский");
            string executable = Path.Combine(epicRoot, @"Portal\Binaries\Win64\EpicGamesLauncher.exe");
            string shortcutPath = Path.Combine(fixture, "Epic test.lnk");
            string state = Path.Combine(fixture, "backup.json");
            string arguments = "-silent Ω 中文 日本語 العربية Русский 😀";
            Directory.CreateDirectory(Path.GetDirectoryName(executable));
            File.WriteAllText(executable, "fixture; never execute");
            var routes = typeof(EpicConnectionSetup).Assembly.GetType("PlayniteUpdateStatus.Launchers.Epic.EpicStartupRoutes");
            var linkInterface = routes.GetNestedType("IShellLinkW", BindingFlags.NonPublic);
            var shortcutType = routes.GetNestedType("Shortcut", BindingFlags.NonPublic);
            object native = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            try
            {
                linkInterface.GetMethod("SetPath").Invoke(native, new object[] { executable });
                linkInterface.GetMethod("SetArguments").Invoke(native, new object[] { arguments });
                linkInterface.GetMethod("SetDescription").Invoke(native, new object[] { "original description" });
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)native).Save(shortcutPath, true);
            }
            finally { Marshal.FinalReleaseComObject(native); }
            try
            {
                var setup = new EpicConnectionSetup(state);
                setup.Enable(epicRoot);
                Eq(true, setup.Configured(epicRoot));
                Eq(1, setup.Load().Changes.Count);
                Eq(arguments, setup.Load().Changes.Single().Before);
                Eq(true, setup.Load().Changes.Single().After.Contains("--remote-debugging-address=127.0.0.1"));
                using (var shortcut = (IDisposable)Activator.CreateInstance(shortcutType, new object[] { shortcutPath }))
                {
                    var link = shortcutType.GetField("link", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shortcut);
                    linkInterface.GetMethod("SetDescription").Invoke(link, new object[] { "later user edit" });
                    shortcutType.GetMethod("Save").Invoke(shortcut, null);
                }
                setup.Remove();
                using (var shortcut = (IDisposable)Activator.CreateInstance(shortcutType, new object[] { shortcutPath }))
                {
                    Eq(arguments, shortcutType.GetProperty("Arguments").GetValue(shortcut));
                    Eq(executable, shortcutType.GetProperty("Target").GetValue(shortcut));
                    var description = new System.Text.StringBuilder(256);
                    var link = shortcutType.GetField("link", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shortcut);
                    linkInterface.GetMethod("GetDescription").Invoke(link, new object[] { description, description.Capacity });
                    Eq("later user edit", description.ToString());
                }
                Eq(false, File.Exists(state));
            }
            finally { Directory.Delete(fixture, true); }
        });
        Test("graceful restart primitive shuts down only its registered fixture process", () => {
            string fixture = Path.Combine(Path.GetTempPath(), "Restart-fixture-" + Guid.NewGuid());
            Directory.CreateDirectory(fixture);
            var children = new System.Collections.Generic.List<Process>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                        "--connection-restart-child \"" + Path.Combine(fixture, i + ".txt") + "\"")
                    { UseShellExecute = false, CreateNoWindow = true };
                    children.Add(Process.Start(start));
                }
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(Path.Combine(fixture, "0.txt")) || !File.Exists(Path.Combine(fixture, "1.txt")))
                {
                    if (DateTime.UtcNow > until) throw new Exception("Fixture did not initialize");
                    System.Threading.Thread.Sleep(50);
                }
                try { typeof(LauncherConnectionActions).GetMethod("ShutdownNormally", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { children[0] }); }
                catch (TargetInvocationException error) { throw error.InnerException; }
                Eq(true, children[0].WaitForExit(10000));
                Eq(false, children[1].HasExited);
                Eq(true, File.ReadAllText(Path.Combine(fixture, "0.txt")).Contains("session ending"));
            }
            finally
            {
                for (int index = 0; index < children.Count; index++)
                {
                    var child = children[index];
                    if (!child.HasExited)
                    {
                        File.WriteAllText(Path.Combine(fixture, index + ".txt.stop"), "stop test fixture");
                        child.WaitForExit(5000);
                    }
                    child.Dispose();
                }
                Directory.Delete(fixture, true);
            }
        });
        Test("complete restart closes an isolated Windows client and starts a new instance", () => {
            string fixture = Path.Combine(Path.GetTempPath(), "Restart-complete-" + Guid.NewGuid());
            Directory.CreateDirectory(fixture);
            string status = Path.Combine(fixture, "status.txt");
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdateStatus.RestartFixture.exe");
            string arguments = "\"" + status + "\"";
            Process initial = null, restarted = null;
            try
            {
                initial = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(status))
                {
                    if (DateTime.UtcNow >= until) throw new Exception("Initial restart fixture did not open");
                    System.Threading.Thread.Sleep(50);
                }
                Eq(initial.Id.ToString(), File.ReadAllText(status));
                var method = typeof(LauncherConnectionActions).GetMethod("RestartAsync", BindingFlags.Static | BindingFlags.NonPublic);
                var restart = (System.Threading.Tasks.Task)method.Invoke(null, new object[] { executable, arguments, true, false });
                restart.GetAwaiter().GetResult();
                Eq(true, initial.HasExited);
                until = DateTime.UtcNow.AddSeconds(10);
                while (File.ReadAllText(status) == initial.Id.ToString())
                {
                    if (DateTime.UtcNow >= until) throw new Exception("Restart did not create a new instance");
                    System.Threading.Thread.Sleep(50);
                }
                int newId = int.Parse(File.ReadAllText(status));
                Eq(false, newId == initial.Id);
                restarted = Process.GetProcessById(newId);
                Eq(false, restarted.HasExited);
            }
            finally
            {
                File.WriteAllText(status + ".stop", "stop fixture");
                if (initial != null) { initial.WaitForExit(5000); initial.Dispose(); }
                if (restarted != null) { restarted.WaitForExit(5000); restarted.Dispose(); }
                Directory.Delete(fixture, true);
            }
        });
        Test("Epic-style restart closes a cooperative client without forcing it", () => EpicRestartFixture(false));
        Test("Epic-style restart terminates an uncooperative client and preserves a bystander", () => EpicRestartFixture(true));
        Test("cold open never terminates an already running client", () => EpicRestartRefusal(false));
        Test("automatic restart never terminates a client from another installation", () => EpicRestartRefusal(true));
        Test("Epic connection setup helper rejects another Windows identity without mutations", () => {
            string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LauncherConnectionSetup.exe");
            using (var child = Process.Start(new ProcessStartInfo(helper, "remove \"C:\\unused\" S-1-0-0") {
                UseShellExecute = false, CreateNoWindow = true }))
            {
                Eq(true, child.WaitForExit(10000)); Eq(1, child.ExitCode);
            }
        });
    }
    private static void EpicRestartFixture(bool ignoreClose)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "Epic-restart-" + Guid.NewGuid());
        Directory.CreateDirectory(fixture);
        string status = Path.Combine(fixture, "main.txt"), other = Path.Combine(fixture, "bystander.txt");
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdateStatus.RestartFixture.exe");
        string arguments = "\"" + status + "\"" + (ignoreClose ? " ignore-close" : "");
        Process initial = null, bystander = null;
        try
        {
            initial = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            bystander = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--connection-restart-child \"" + other + "\"") { UseShellExecute = false, CreateNoWindow = true });
            WaitForFixture(() => File.Exists(status) && File.Exists(other));
            InvokeFixtureRestart(executable, arguments, true, true).GetAwaiter().GetResult();
            Eq(true, initial.HasExited);
            Eq(ignoreClose ? -1 : 0, initial.ExitCode);
            Eq(false, bystander.HasExited);
            WaitForFixture(() => File.ReadAllText(status) != initial.Id.ToString());
            int newId = int.Parse(File.ReadAllText(status));
            using (var restarted = Process.GetProcessById(newId)) Eq(false, restarted.HasExited);
            Eq(true, File.Exists(status + ".close-requested"));
        }
        finally
        {
            StopRestartFixture(status, initial);
            StopRestartFixture(other, bystander);
            Directory.Delete(fixture, true);
        }
    }
    private static void EpicRestartRefusal(bool wrongInstallation)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "Epic-restart-refusal-" + Guid.NewGuid());
        Directory.CreateDirectory(fixture);
        string status = Path.Combine(fixture, "main.txt");
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdateStatus.RestartFixture.exe");
        string arguments = "\"" + status + "\" ignore-close";
        Process initial = null;
        try
        {
            initial = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            WaitForFixture(() => File.Exists(status));
            string requested = wrongInstallation ? Path.Combine(fixture, Path.GetFileName(executable)) : executable;
            try
            {
                InvokeFixtureRestart(requested, arguments, wrongInstallation, true).GetAwaiter().GetResult();
                throw new Exception("Restart must refuse this target");
            }
            catch (LauncherStillRunningException) { Eq(false, wrongInstallation); }
            catch (IOException error) { Eq(true, wrongInstallation); Eq(true, error.Message.Contains("another installation")); }
            Eq(false, initial.HasExited);
            Eq(initial.Id.ToString(), File.ReadAllText(status));
            Eq(false, File.Exists(status + ".close-requested"));
        }
        finally { StopRestartFixture(status, initial); Directory.Delete(fixture, true); }
    }
    private static System.Threading.Tasks.Task InvokeFixtureRestart(string executable, string arguments, bool close, bool terminate)
    {
        return (System.Threading.Tasks.Task)typeof(LauncherConnectionActions).GetMethod("RestartAsync",
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { executable, arguments, close, terminate });
    }
    private static void WaitForFixture(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new Exception("Restart fixture did not become ready");
            System.Threading.Thread.Sleep(50);
        }
    }
    private static void StopRestartFixture(string status, Process initial)
    {
        File.WriteAllText(status + ".stop", "stop disposable test fixture");
        if (initial != null) { initial.WaitForExit(5000); initial.Dispose(); }
        if (File.Exists(status))
        {
            int id;
            if (int.TryParse(File.ReadAllText(status), out id))
            {
                try { using (var restarted = Process.GetProcessById(id)) restarted.WaitForExit(5000); }
                catch (ArgumentException) { }
            }
        }
    }
    private static int ConnectionRestartChild(string status)
    {
        var app = new Application();
        var window = new Window { Width = 100, Height = 100, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false };
        window.Loaded += (s,e) => File.WriteAllText(status, "ready");
        app.SessionEnding += (s,e) => File.AppendAllText(status, "|session ending");
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (s,e) => {
            if (File.Exists(status + ".stop") || elapsed.Elapsed >= TimeSpan.FromSeconds(30)) window.Close();
        };
        timer.Start();
        return app.Run(window);
    }
}
