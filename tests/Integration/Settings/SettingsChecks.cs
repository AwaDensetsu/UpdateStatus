using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting.Messaging;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;
using static UpdateStatus.Tests.Support.SteamSamples;

internal static partial class Program
{
    private static void CoverBadgeSettings()
    {
        var settings = Playnite.SDK.Data.Serialization.FromJson<PlayniteUpdateStatus.Settings>("{\"CoverBadges\":false}");
        Eq(false, settings.CoverBadges);
        var settingsModel = new PlayniteUpdateStatus.SettingsViewModel(settings);
        settingsModel.BeginEdit();
        settings.CoverBadges = true;
        settingsModel.CancelEdit();
        Eq(false, settings.CoverBadges);
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        {
            plugin.Settings.Enabled = plugin.Settings.CoverBadges = true;
            var view = BindSettingsView(plugin.GetSettingsView(false));
            ((TabControl)view.FindName("LauncherSettingsTabs")).SelectedIndex = 0;
        var panel = (StackPanel)view.FindName("GeneralSettingsPanel");
            var toggle = panel.Children.OfType<CheckBox>().Single(check => ((string)check.Content).StartsWith("Show update indicators"));
            var connect = (Button)view.FindName("SteamConnectButton");
            Eq(true, connect != null);
            Eq("Set up connection", connect.Content);
            Eq("Show update indicators on covers and lists", toggle.Content);
            Eq(true, panel.Children.OfType<CheckBox>().Any(check => Equals(check.Content, "Show update status")));
            Eq(false, panel.Children.OfType<CheckBox>().Any(check => ((string)check.Content).Contains("Aniki") || ((string)check.Content).Contains("(Desktop and Fullscreen)")));
            Eq((PropertyInfo)null, typeof(PlayniteUpdateStatus.Settings).GetProperty("AnikiIntegration"));
            Eq((PropertyInfo)null, typeof(PlayniteUpdateStatus.Settings).GetProperty("DebugLogging"));
            Eq(false, panel.Children.OfType<CheckBox>().Any(check => ((string)check.Content).Contains("Debug")));
            Eq((Type)null, typeof(PlayniteUpdateStatus.Plugin).Assembly.GetType("PlayniteUpdateStatus.UI.AnikiIntegration"));
            panel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 30, 51));
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.White));
            panel.Resources[typeof(TextBlock)] = textStyle;
            var checkStyle = new Style(typeof(CheckBox));
            checkStyle.Setters.Add(new Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.White));
            panel.Resources[typeof(CheckBox)] = checkStyle;
            var window = new TestWindow
            {
                Content = view,
                Width = 600,
                Height = 650,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var coverBinding = BindingOperations.GetBindingBase(toggle, CheckBox.IsCheckedProperty);
                plugin.GetSettings(false).BeginEdit();
                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                toggle.GetBindingExpression(CheckBox.IsCheckedProperty).UpdateSource();
                Eq(false, plugin.Settings.CoverBadges);
                Eq(coverBinding, BindingOperations.GetBindingBase(toggle, CheckBox.IsCheckedProperty));
                plugin.GetSettings(false).CancelEdit();
                Eq(true, plugin.Settings.CoverBadges);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Eq(true, toggle.IsChecked);
                toggle.GetBindingExpression(CheckBox.IsCheckedProperty).UpdateTarget();
                Eq(true, toggle.IsChecked);
                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                toggle.GetBindingExpression(CheckBox.IsCheckedProperty).UpdateSource();
                plugin.GetSettings(false).EndEdit();
                Eq(false, plugin.Settings.CoverBadges);
                Eq(false, toggle.IsChecked);
                window.UpdateLayout();
                Render(panel, "shared-settings-1.0.0.png");
                var menu = plugin.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(item => item.Description == "Show update indicators");
                menu.Action(null);
                Eq(true, plugin.Settings.CoverBadges);
                calls.Mode = ApplicationMode.Fullscreen;
                menu = plugin.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(item => item.Description == "Hide update indicators");
                menu.Action(null);
                Eq(false, plugin.Settings.CoverBadges);
            }
            finally
            {
                window.Close();
            }
        }

        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            Eq(false, plugin.Settings.CoverBadges);
    }

    private static void SteamLocationPersistence()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SteamLocation-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string first = Path.Combine(folder, "Steam A"), second = Path.Combine(folder, "Steam B"), data = Path.Combine(folder, "userdata", "steam-location.v1");
        string oldCache = SteamLocator.CachedRoot;
        int logs = 0;
        Action<string, bool> log = (s, d) => logs++;
        Action<string> remember = null;
        try
        {
            foreach (string root in new[]
            {
                first,
                second
            }

            )
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "steam.exe"), "never execute");
                File.WriteAllText(Path.Combine(root, "steamclient64.dll"), "metadata fixture");
            }

            var store = new SteamLocationStore(data, log);
            Eq((string)null, store.Folder);
            // Earlier catalog disposal can leave a discovery read in flight.
            // This fixture measures its own synchronous candidate transitions.
            int fixtureThread = Thread.CurrentThread.ManagedThreadId;
            remember = root =>
            {
                if (Thread.CurrentThread.ManagedThreadId == fixtureThread)
                    store.Remember(root);
            };
            SteamLocator.Located += remember;
            Eq(first, SteamLocator.ResolveCandidates(new[] { null, "", @"C:relative", "bad\0path", Path.Combine(folder, "missing"), first + "\\" }));
            Eq(first, store.Folder);
            Eq(first, SteamLocator.CachedRoot);
            Eq(first, new SteamLocationStore(data, log).Folder);
            var stamp = File.GetLastWriteTimeUtc(data);
            store.Remember(first + "\\");
            Eq(stamp, File.GetLastWriteTimeUtc(data));
            Eq(second, SteamLocator.ResolveCandidates(new[] { second, first }));
            Eq(second, new SteamLocationStore(data, log).Folder);
            File.Delete(Path.Combine(second, "steam.exe"));
            Eq((string)null, new SteamLocationStore(data, log).Folder);
            Eq(first, SteamLocator.ResolveCandidates(new[] { second, first }));
            Eq(first, new SteamLocationStore(data, log).Folder);
            Eq((string)null, SteamLocator.ResolveCandidates(new[] { second, "relative" }));
            Eq(0, Directory.GetFiles(Path.GetDirectoryName(data), "*.tmp").Length);
            File.WriteAllText(data, "bad header\n" + first);
            Eq((string)null, new SteamLocationStore(data, log).Folder);
            File.WriteAllText(data, new string('x', 8193));
            Eq((string)null, new SteamLocationStore(data, log).Folder);
            var unavailable = new SteamLocationStore(folder, log);
            unavailable.Remember(first);
            Eq((string)null, unavailable.Folder);
            Eq(true, logs >= 3);
        }
        finally
        {
            if (remember != null)
                SteamLocator.Located -= remember;
            SteamLocator.CachedRoot = oldCache;
            Directory.Delete(folder, true);
        }
    }

    private static void FreshInstallationPaths()
    {
        string scope = Path.Combine(Path.GetTempPath(), "FreshPluginPaths-" + Guid.NewGuid());
        Directory.CreateDirectory(scope);
        string oldManual = SteamLocator.ManualRoot, oldCache = SteamLocator.CachedRoot;
        var constructor = typeof(PlayniteUpdateStatus.Plugin).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IPlayniteAPI), typeof(Func<string>) }, null);
        try
        {
            string firstRecord = null;
            for (int pc = 1; pc <= 2; pc++)
            {
                string steam = Path.Combine(scope, "PC " + pc, "Games folder " + pc, "Steam"), data = Path.Combine(scope, "PC " + pc, "Playnite data");
                Directory.CreateDirectory(steam);
                File.WriteAllText(Path.Combine(steam, "steam.exe"), "metadata fixture; never execute");
                File.WriteAllText(Path.Combine(steam, "steamclient64.dll"), "metadata fixture; never execute");
                var calls = new ApiCalls
                {
                    Dispatcher = Dispatcher.CurrentDispatcher,
                    DataPath = data,
                    Mode = pc == 1 ? ApplicationMode.Desktop : ApplicationMode.Fullscreen
                };
                var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
                int discoveries = 0;
                Func<string> find = () =>
                {
                    discoveries++;
                    return steam;
                };
                using (var plugin = (PlayniteUpdateStatus.Plugin)constructor.Invoke(new object[] { api, find }))
                {
                    Eq(1, discoveries);
                    Eq("", plugin.Settings.SteamFolder);
                    Eq(steam, SteamLocator.CachedRoot);
                    Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
                    Eq(false, plugin.SteamCatalog.Current.Live);
                    string file = Directory.GetFiles(data, "steam-location.v1", SearchOption.AllDirectories).Single();
                    Eq(steam, File.ReadAllLines(file)[1]);
                    Eq(true, Path.GetFullPath(file).StartsWith(Path.GetFullPath(data) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    if (pc == 1)
                        firstRecord = file;
                    else
                        Eq(false, File.ReadAllText(file).Contains("PC 1"));
                    Eq(0, calls.Starts);
                }

                // Restart keeps this PC's location even when a fresh scan has no result.
                using (var plugin = (PlayniteUpdateStatus.Plugin)constructor.Invoke(new object[] { api, new Func<string>(() => null) }))
                {
                    Eq(steam, SteamLocator.CachedRoot);
                    Eq("", plugin.Settings.SteamFolder);
                }
            }

            Eq(true, File.ReadAllText(firstRecord).Contains("PC 1"));
            string missingData = Path.Combine(scope, "PC 3", "Playnite data");
            var missingCalls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher,
                DataPath = missingData
            };
            var missingApi = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), missingCalls).GetTransparentProxy();
            using (var plugin = (PlayniteUpdateStatus.Plugin)constructor.Invoke(new object[] { missingApi, new Func<string>(() => null) }))
            {
                Eq((string)null, SteamLocator.CachedRoot);
                Eq("", plugin.Settings.SteamFolder);
                Eq(0, Directory.GetFiles(missingData, "steam-location.v1", SearchOption.AllDirectories).Length);
                string appeared = Path.Combine(scope, "PC 2", "Games folder 2", "Steam");
                SteamLocator.ResolveCandidates(new[] { appeared });
                Eq(appeared, File.ReadAllLines(Directory.GetFiles(missingData, "steam-location.v1", SearchOption.AllDirectories).Single())[1]);
                Eq(0, missingCalls.Starts);
            }
        }
        finally
        {
            SteamLocator.ManualRoot = oldManual;
            SteamLocator.CachedRoot = oldCache;
            Directory.Delete(scope, true);
        }
    }

    private static void SteamFolderStatusView()
    {
        string root = Path.Combine(Path.GetTempPath(), "SteamStatus-" + Guid.NewGuid(), "Steam with spaces");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"steam.exe"), "metadata fixture only");
        File.WriteAllText(Path.Combine(root,"steamclient64.dll"), "metadata fixture only");
        var settings = new PlayniteUpdateStatus.Settings();
        string available = null; int scans = 0;
        var model = new PlayniteUpdateStatus.SettingsViewModel(settings, () => { scans++; return available; }, launcherSession: _ => "");
        var view = BindSettingsView(new SettingsView(model));
        var panel = (StackPanel)view.FindName("SteamSettingsPanel");
        var status = (TextBlock)view.FindName("SteamFolderStatus");
        var input = (TextBlock)view.FindName("SteamFolderInput");
        var card = (LauncherConnectionCard)view.FindName("SteamCardControl");
        var window = new TestWindow { Content = view, Width = 680, Height = 740, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false, Background = System.Windows.Media.Brushes.DarkSlateGray,
            Foreground = System.Windows.Media.Brushes.White };
        try
        {
            Eq("Steam not found", status.Text); Eq("", input.Text);
            window.Show(); window.UpdateLayout();
            available = root; PumpFolderStatus(() => input.Text == root);
            Eq("✓ Steam detected", status.Text); Eq("", settings.SteamFolder);
            Eq(root, input.ToolTip); Eq(TextTrimming.CharacterEllipsis, input.TextTrimming);
            Render(view,"steam-folder-settings.png");
            window.Width = 360; window.UpdateLayout();
            Eq(true,card.ActualWidth <= panel.ActualWidth + 1);
            model.FolderInput = root + Path.DirectorySeparatorChar;
            Eq("Manual Steam folder selected", model.FolderStatus);
            model.FolderInput = Path.Combine(root,"missing");
            Eq("Selected Steam folder is invalid",model.FolderStatus);
            model.FolderInput = "";
            Eq("",settings.SteamFolder); Eq(root,model.FolderInput);
            available = null; PumpFolderStatus(() => input.Text == "");
            Render(view,"steam-folder-missing.png");
            available = root; PumpFolderStatus(() => input.Text == root);
            window.Close(); int afterClose = scans;
            var until = DateTime.UtcNow.AddMilliseconds(2200);
            while (DateTime.UtcNow < until)
            { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(() => {})); Thread.Sleep(20); }
            Eq(afterClose,scans);
            var missing = new PlayniteUpdateStatus.Settings { SteamFolder = Path.Combine(root,"missing") };
            var missingModel = new PlayniteUpdateStatus.SettingsViewModel(missing);
            missingModel.BeginEdit();
            var missingView = BindSettingsView(new SettingsView(missingModel,() => null));
            Eq("",((TextBlock)missingView.FindName("SteamFolderInput")).Text);
            Eq("",missing.SteamFolder);
            missingModel.CancelEdit(); Eq(Path.Combine(root,"missing"),missing.SteamFolder);
        }
        finally { window.Close(); Directory.Delete(Path.GetDirectoryName(root),true); }
    }

    private static void PumpFolderStatus(Func<bool> condition)
    {
        var stop = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < stop)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
            {
            }));
            Thread.Sleep(20);
        }

        Eq(true, condition());
    }

    private static void ManualSteamFolder()
    {
        string original = SteamLocator.ManualRoot;
        string cached = SteamLocator.CachedRoot;
        string folder = Path.Combine(Path.GetTempPath(), "ManualSteam-" + Guid.NewGuid(), "Steam with spaces");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "steam.exe"), "metadata fixture; never execute");
        File.WriteAllText(Path.Combine(folder, "steamclient64.dll"), "unverified fixture");
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            DataPath = Path.Combine(folder, "plugin-data"),
            FolderChoice = folder
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        try
        {
            var migrated = Playnite.SDK.Data.Serialization.FromJson<PlayniteUpdateStatus.Settings>("{\"Enabled\":true}");
            Eq("", migrated.SteamFolder);
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                plugin.Settings.Enabled = false; // settings tests do not start a reader
                plugin.GetSettings(false).BeginEdit();
                plugin.Settings.SteamFolder = folder;
                plugin.GetSettings(false).CancelEdit();
                Eq("", plugin.Settings.SteamFolder);
                plugin.Settings.SteamFolder = Path.Combine(folder, "missing");
                List<string> errors;
                Eq(false, plugin.GetSettings(false).VerifySettings(out errors));
                plugin.Settings.SteamFolder = "relative-folder";
                Eq(false, plugin.GetSettings(false).VerifySettings(out errors));
                plugin.Settings.SteamFolder = folder + Path.DirectorySeparatorChar;
                Eq(true, plugin.GetSettings(false).VerifySettings(out errors));
                Eq(folder, plugin.Settings.SteamFolder);
                plugin.GetSettings(false).EndEdit();
                Eq(folder, new SteamLocator().FindRoot());
                Eq(0, calls.Starts);
            }

            foreach (var mode in new[]
            {
                ApplicationMode.Desktop,
                ApplicationMode.Fullscreen
            }

            )
            {
                calls.Mode = mode;
                using (var plugin = new PlayniteUpdateStatus.Plugin(api))
                {
                    Eq(folder, plugin.Settings.SteamFolder);
                    Eq(folder, new SteamLocator().FindRoot());
                    plugin.Settings.SteamFolder = "";
                    var panel = (StackPanel)BindSettingsView(new SettingsView((PlayniteUpdateStatus.SettingsViewModel)plugin.GetSettings(false), () => folder)).FindName("SteamSettingsPanel");
                    Eq(folder, ((TextBlock)((LauncherConnectionCard)panel.Children.OfType<LauncherConnectionCard>().Single()).FindName("FolderPath")).Text);
                    Eq("", plugin.Settings.SteamFolder);
                    var browse = (Button)panel.Children.OfType<LauncherConnectionCard>().Single().FindName("FolderButton");
                    browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Eq(folder, plugin.Settings.SteamFolder);
                    plugin.Settings.SteamFolder = "";
                    plugin.GetSettings(false).EndEdit();
                    Eq("", SteamLocator.ManualRoot);
                    plugin.Settings.SteamFolder = folder;
                    plugin.GetSettings(false).EndEdit();
                }
            }

            SteamLocator.ManualRoot = Path.Combine(folder, "missing");
            Eq(false, new SteamLocator().FindRoot() == SteamLocator.ManualRoot);
            Eq(@"D:\", SteamLocator.NormalizeRoot(@"D:\"));
            Eq("\"D:\\\\\"", SteamLocator.QuoteRoot(@"D:\"));
            bool rejected = false;
            try
            {
                SteamLocator.QuoteRoot("bad\"path");
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Eq(true, rejected);
        }
        finally
        {
            SteamLocator.ManualRoot = original;
            SteamLocator.CachedRoot = cached;
            Directory.Delete(Path.GetDirectoryName(folder), true);
        }
    }
}
