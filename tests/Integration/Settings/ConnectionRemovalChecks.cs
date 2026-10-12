using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Core;
using Plugin = PlayniteUpdateStatus.Plugin;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

internal static partial class Program
{
    private static void ConnectionRemovalChecks()
    {
        foreach (bool epic in new[] { false, true })
        {
            bool provider = epic;
            Test("connection removal survives settings Cancel and SDK reload: " + (epic ? "Epic" : "Steam"), () => RemovalSettingsPersistence(provider));
        }
        Test("Steam removal deletes stored badges and rejects cached data across plugin reload", RemovalPurgesPluginInformation);
        Test("removing Steam cancels retained Play without another queue command or automatic launch", RemovalCancelsSelectedPlay);
        Test("removed providers forward normal SDK Play without opening an update observer", RemovedProviderNormalPlay);
    }
    private static void RemovalSettingsPersistence(bool epic)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Removal-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(directory, "steamclient64.dll"), "fixture; never load");
            SteamUiAccess.Enable(directory);
            bool configured = true;
            int applied = 0;
            Settings saved = null;
            var settings = new Settings { EpicEnabled = true };
            var model = new SettingsViewModel(settings, () => directory,
                findEpicFolder: _ => directory, epicConfigured: _ => configured, epicHasBackup: () => configured,
                disableAccess: root => { File.Delete(Path.Combine(root,".cef-enable-remote-debugging")); return Task.CompletedTask; },
                configureEpic: (root, remove) => { configured = !remove; return Task.CompletedTask; },
                save: value => saved = Serialization.GetClone(value), apply: () => applied++, launcherSession: _ => "");
            model.InitializeLocation();
            var location = model.RefreshEpicLocationAsync();
            PumpFolderStatus(() => location.IsCompleted); location.GetAwaiter().GetResult();
            model.BeginEdit(); settings.CoverBadges = false;
            var removal = epic ? model.RemoveEpicConnectionAsync() : model.RemoveSteamConnectionAsync();
            PumpFolderStatus(() => removal.IsCompleted); removal.GetAwaiter().GetResult();
            Eq(1, applied); Eq(true, saved.CoverBadges);
            Eq(true, epic ? saved.EpicConnectionRemoved : saved.SteamConnectionRemoved);
            Eq(false, epic ? saved.SteamConnectionRemoved : saved.EpicConnectionRemoved);
            model.CancelEdit();
            Eq(true, settings.CoverBadges);
            Eq(true, epic ? settings.EpicConnectionRemoved : settings.SteamConnectionRemoved);
            var reloaded = Serialization.FromJson<Settings>(Serialization.ToJson(saved));
            Eq(true, epic ? reloaded.EpicConnectionRemoved : reloaded.SteamConnectionRemoved);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void RemovalPurgesPluginInformation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Removal-plugin-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var calls = new ApiCalls { Dispatcher = Dispatcher.CurrentDispatcher, DataPath = directory };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        var changed = typeof(SteamUpdateCatalog).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            using (var plugin = new Plugin(api))
            {
                plugin.Settings.Enabled = true;
                var snapshot = CoverCatalog();
                ((Action<SteamCatalogSnapshot>)changed.GetValue(plugin.SteamCatalog))(snapshot);
                Eq(3, plugin.CoverHistory.PendingAppIds.Count);
                var badge = (CoverBadgeControl)plugin.GetGameViewControl(new GetGameViewControlArgs { Name = "Badge" });
                badge.GameContext = CoverGame(550); badge.Refresh();
                Eq(Visibility.Visible, badge.Visibility);
                plugin.Settings.SteamConnectionRemoved = true;
                plugin.ApplySettings();
                Eq(0, plugin.CoverHistory.PendingAppIds.Count);
                Eq(0, Directory.GetFiles(directory, "steam-update-badges.v1", SearchOption.AllDirectories).Length);
                Eq(false, plugin.SteamCatalog.Current.Live);
                Eq(Visibility.Collapsed, badge.Visibility);
                ((Action<SteamCatalogSnapshot>)changed.GetValue(plugin.SteamCatalog))(CoverCatalog());
                Eq(0, plugin.CoverHistory.PendingAppIds.Count);
                badge.Refresh(); Eq(Visibility.Collapsed, badge.Visibility);
            }
            using (var plugin = new Plugin(api))
            {
                // SDK serialization of the removed flag is exercised separately.
                plugin.Settings.SteamConnectionRemoved = true; plugin.ApplySettings();
                Eq(0, plugin.CoverHistory.PendingAppIds.Count);
                var badge = (CoverBadgeControl)plugin.GetGameViewControl(new GetGameViewControlArgs { Name = "Badge" });
                badge.GameContext = CoverGame(550); badge.Refresh(); Eq(Visibility.Collapsed, badge.Visibility);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void RemovalCancelsSelectedPlay()
    {
        var calls = new ApiCalls { Dispatcher = Dispatcher.CurrentDispatcher };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var fixture = new SelectionFixture(550))
        using (var plugin = new Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, fixture } });
            var args = Event<OnGameStartingEventArgs>(SelectedGame(550));
            ThreadPool.QueueUserWorkItem(_ => { try { plugin.OnGameStarting(args); } finally { returned.Set(); } });
            try
            {
                Until(() => plugin.Monitor.Current.CanPause);
                int commands = fixture.Commands;
                // Removing Epic must not cancel a retained Steam Play.
                plugin.Settings.EpicConnectionRemoved = true; plugin.ApplySettings();
                Eq(UpdatePhase.Updating, plugin.Monitor.Current.Phase);
                Eq(false, returned.WaitOne(100));
                plugin.Settings.SteamConnectionRemoved = true; plugin.ApplySettings();
                Eq(true, returned.WaitOne(2000)); Eq(true, args.CancelStartup);
                Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
                Eq(commands, fixture.Commands); Eq(0, calls.Starts);
                plugin.Settings.SteamConnectionRemoved = false; plugin.ApplySettings();
                Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase); Eq(0, calls.Starts);
            }
            finally { plugin.Monitor.Stop(Guid.Empty); returned.WaitOne(3000); }
        }
    }
    private static void RemovedProviderNormalPlay()
    {
        var calls = new ApiCalls { Dispatcher = Dispatcher.CurrentDispatcher };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var fixture = new SelectionFixture(550))
        using (var plugin = new Plugin(api))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, fixture } });
            plugin.Settings.SteamConnectionRemoved = plugin.Settings.EpicConnectionRemoved = true;
            plugin.ApplySettings();
            var steam = Event<OnGameStartingEventArgs>(SelectedGame(550));
            plugin.OnGameStarting(steam);
            Eq(false, steam.CancelStartup); Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
            Eq(0, fixture.Selections);
            var epic = Event<OnGameStartingEventArgs>(new Game { Id = Guid.NewGuid(), Name = "Epic fixture", GameId = "fixture-app",
                PluginId = new Guid("00000002-DBD1-46C6-B5D0-B1BA559D10E4") });
            plugin.OnGameStarting(epic);
            Eq(false, epic.CancelStartup); Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase); Eq(0, calls.Starts);
        }
    }
}
