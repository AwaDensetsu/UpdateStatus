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
    private static SteamCatalogSnapshot OfflineCoverCatalog()
    {
        return new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture"), new SteamCatalogApp(551, "fixture"), new SteamCatalogApp(552, "fixture") }, null, "Steam closed");
    }

    private static void CoverHistoryPersistence()
    {
        string folder = Path.Combine(Path.GetTempPath(), "UpdateBadgeHistory-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "steam-update-badges.v1");
        try
        {
            var history = new SteamCoverHistory(path, (s, d) =>
            {
            });
            Eq(0, history.PendingAppIds.Count);
            history.Observe(CoverCatalog());
            Eq(3, history.PendingAppIds.Count);
            Eq(true, File.Exists(path));
            var stamp = File.GetLastWriteTimeUtc(path);
            history.Observe(CoverCatalog());
            Eq(stamp, File.GetLastWriteTimeUtc(path));
            history = new SteamCoverHistory(path, (s, d) =>
            {
            });
            foreach (uint id in new uint[]
            {
                550,
                551,
                552
            }

            )
                Eq("Update Required", new CoverBadgeSnapshot(OfflineCoverCatalog(), history).Find(CoverGame(id)).Text);
            history.Observe(OfflineCoverCatalog());
            Eq(3, history.PendingAppIds.Count);
            var previousDisplay = new CoverBadgeSnapshot(OfflineCoverCatalog(), history);
            // Downloads completed outside Playnite: fresh client rows clear only those apps.
            history.Observe(CoverCatalog("550:4:-1,551:20:-1,552:1542:-1", "0", "0"));
            Eq(1, history.PendingAppIds.Count);
            Eq("Update Required", previousDisplay.Find(CoverGame(550)).Text);
            Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(OfflineCoverCatalog(), history).Find(CoverGame(550)));
            history = new SteamCoverHistory(path, (s, d) =>
            {
            });
            Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(OfflineCoverCatalog(), history).Find(CoverGame(550)));
            Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(OfflineCoverCatalog(), history).Find(CoverGame(551)));
            Eq("Update Required", new CoverBadgeSnapshot(OfflineCoverCatalog(), history).Find(CoverGame(552)).Text);
            history.Observe(CoverCatalog("550:4:-1,551:4:-1,552:4:-1", "0", "0"));
            Eq(0, new SteamCoverHistory(path, (s, d) =>
            {
            }).PendingAppIds.Count);
            Eq(1, Directory.GetFiles(folder).Length); // atomic saves leave no temporary files
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void CoverHistoryUnavailable()
    {
        var history = new SteamCoverHistory(null, (s, d) =>
        {
        });
        var oldSample = CatalogFrame("550:6:-1,551:1294:0,552:1542:-1", new uint[] { 550, 551, 552 }, "1", "551", "1");
        oldSample.ReceivedUtc = DateTime.UtcNow.AddSeconds(-1);
        var oldRequired = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture"), new SteamCatalogApp(551, "fixture"), new SteamCatalogApp(552, "fixture") }, oldSample, null);
        history.Observe(oldRequired);
        history.Observe(CoverCatalog("550:-:-,551:0:-1,552:2147483648:-1", "0", "0"));
        Eq(3, history.PendingAppIds.Count);
        history.Observe(new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture") }, CatalogFrame("550:4:-1", new uint[] { 550 }), null));
        Eq(2, history.PendingAppIds.Count); // missing rows retain their previous state
        history.Observe(oldRequired);
        Eq(2, history.PendingAppIds.Count); // older replies cannot restore a completed update
        var apps = new[]
        {
            new SteamCatalogApp(551, "fixture")
        };
        var frame = CatalogFrame("551:4:-1", new uint[] { 551 });
        frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(-6);
        var stale = new SteamCatalogSnapshot(apps, frame, null);
        history.Observe(stale);
        Eq(2, history.PendingAppIds.Count);
        Eq("Update Required", new CoverBadgeSnapshot(stale, history).Find(CoverGame(551)).Text);
        frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(5);
        history.Observe(new SteamCatalogSnapshot(apps, frame, null));
        Eq(2, history.PendingAppIds.Count);
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(OfflineCoverCatalog(), history).Find(CoverGame(550)));
        var foreign = CoverGame(551);
        foreign.PluginId = Guid.NewGuid();
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(null, history).Find(foreign));
        foreign.PluginId = SteamGameIdentity.LibraryId;
        foreign.IsRunning = true;
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(null, history).Find(foreign));
        string folder = Path.Combine(Path.GetTempPath(), "UpdateBadgeHistoryInvalid-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "history");
        try
        {
            foreach (string invalid in new[]
            {
                "SteamUpdateBadges|2\n551",
                "SteamUpdateBadges|1\n551\ninvalid",
                "SteamUpdateBadges|1\n551\n551",
                "SteamUpdateBadges|1\n0"
            }

            )
            {
                File.WriteAllText(path, invalid);
                Eq(0, new SteamCoverHistory(path, (s, d) =>
                {
                }).PendingAppIds.Count);
            }

            File.WriteAllText(path, "blocked-parent");
            int errors = 0;
            var unavailable = new SteamCoverHistory(Path.Combine(path, "history"), (s, d) => errors++);
            unavailable.Observe(CoverCatalog());
            Eq(3, unavailable.PendingAppIds.Count);
            Eq(true, errors > 0);
            Eq("blocked-parent", File.ReadAllText(path));
            Eq(1, Directory.GetFiles(folder).Length);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void CoverHistorySurfaces()
    {
        var history = new SteamCoverHistory(null, (s, d) =>
        {
        });
        history.Observe(CoverCatalog());
        Image image;
        var tile = CoverTile(CoverGame(551), out image);
        var root = new Grid();
        root.Children.Add(tile);
        var window = new TestWindow
        {
            Content = root,
            Width = 300,
            Height = 330,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        var catalog = OfflineCoverCatalog();
        bool enabled = true;
        var sdk = new CoverBadgeControl(() => catalog, () => enabled, () => history)
        {
            GameContext = CoverGame(551)
        };
        using (var presenter = new CoverBadgePresenter((s, d) =>
        {
        }))
        {
            try
            {
                window.Show();
                window.UpdateLayout();
                presenter.Refresh(window, catalog, true, history);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName(tile.Children.OfType<Canvas>().Single().Children.OfType<CoverBadgeVisual>().Single()));
                sdk.Refresh();
                Eq(Visibility.Visible, sdk.Visibility);
                Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                catalog = CoverCatalog();
                presenter.Refresh(window, catalog, true, history);
                sdk.Refresh();
                Eq("Updating", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                catalog = CoverCatalog("550:4:-1,551:4:-1,552:4:-1", "0", "0");
                history.Observe(catalog);
                presenter.Refresh(window, catalog, true, history);
                sdk.Refresh();
                Eq(0, presenter.ActiveCount);
                Eq(Visibility.Collapsed, sdk.Visibility);
                catalog = OfflineCoverCatalog();
                presenter.Refresh(window, catalog, true, history);
                sdk.Refresh();
                Eq(0, presenter.ActiveCount);
                Eq(Visibility.Collapsed, sdk.Visibility);
                history.Observe(CoverCatalog());
                presenter.Refresh(window, catalog, false, history);
                enabled = false;
                sdk.Refresh();
                Eq(0, presenter.ActiveCount);
                Eq(Visibility.Collapsed, sdk.Visibility);
                Eq(3, history.PendingAppIds.Count);
                presenter.Refresh(window, catalog, true, history);
                window.UpdateLayout();
                enabled = true;
                sdk.Refresh();
                Eq(1, presenter.ActiveCount);
                Eq(Visibility.Visible, sdk.Visibility);
            }
            finally
            {
                sdk.Shutdown();
                window.Close();
            }
        }
    }

    private static void CoverBadgePluginLifecycle()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        var reader = new QueueFixture
        {
            Sample = CatalogFrame("550:6:-1", new uint[] { 550 })
        };
        var catalog = new SteamUpdateCatalog(() => "cover-fixture", () => new[] { new SteamCatalogApp(550, "fixture") }, apps => reader, (s, d) =>
        {
        });
        Image image;
        var game = CoverGame(550);
        var tile = CoverTile(game, out image);
        var root = new Grid();
        root.Children.Add(tile);
        var window = new TestWindow
        {
            Content = root,
            Width = 300,
            Height = 330,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        var previous = Application.Current.MainWindow;
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        {
            plugin.SteamCatalog.Dispose();
            typeof(PlayniteUpdateStatus.Plugin).GetProperty("SteamCatalog").SetValue(plugin, catalog, null);
            catalog.Changed += plugin.CoverHistory.Observe;
            plugin.Settings.Enabled = plugin.Settings.CoverBadges = true;
            var badge = (CoverBadgeControl)plugin.GetGameViewControl(new GetGameViewControlArgs { Name = "Badge" });
            Eq(true, badge != null);
            badge.GameContext = game;
            try
            {
                Application.Current.MainWindow = window;
                window.Show();
                window.UpdateLayout();
                plugin.OnApplicationStarted(new OnApplicationStartedEventArgs());
                // Persisted badges can render before the live catalog arrives.
                PumpUntil(() => catalog.Current.Live && tile.Children.OfType<Canvas>().Any());
                Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
                plugin.OnGameStopped(Event<OnGameStoppedEventArgs>(CoverGame(551)));
                Eq(true, catalog.Current.Live);
                plugin.Settings.CoverBadges = false;
                plugin.ApplySettings();
                Eq(0, tile.Children.OfType<Canvas>().Count());
                Eq(true, catalog.Current.Live);
                plugin.Settings.CoverBadges = true;
                plugin.ApplySettings();
                window.UpdateLayout();
                Eq(1, tile.Children.OfType<Canvas>().Count());
                reader.Sample = CatalogFrame("550:4:-1", new uint[] { 550 });
                catalog.RefreshSoon();
                PumpUntil(() => !tile.Children.OfType<Canvas>().Any());
                reader.Sample = CatalogFrame("550:6:-1", new uint[] { 550 });
                catalog.RefreshSoon();
                PumpUntil(() => tile.Children.OfType<Canvas>().Any());
                plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
                Eq(0, tile.Children.OfType<Canvas>().Count());
                Eq(Visibility.Collapsed, badge.Visibility);
                Eq(true, reader.Disposed);
                Eq(0, calls.Starts);
            }
            finally
            {
                window.Close();
                Application.Current.MainWindow = previous;
            }
        }
    }

    private static void CoverHistoryPluginReload()
    {
        string folder = Path.Combine(Path.GetTempPath(), "UpdateBadgePlugin-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            DataPath = folder
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        var changed = typeof(SteamUpdateCatalog).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                plugin.Settings.CoverBadges = false;
                ((Action<SteamCatalogSnapshot>)changed.GetValue(plugin.SteamCatalog))(CoverCatalog());
                Eq(3, plugin.CoverHistory.PendingAppIds.Count);
                Eq(1, Directory.GetFiles(folder, "steam-update-badges.v1", SearchOption.AllDirectories).Length);
            }

            Action<SteamCatalogSnapshot> late;
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                Eq(3, plugin.CoverHistory.PendingAppIds.Count);
                Eq(false, plugin.SteamCatalog.Current.Live);
                plugin.Settings.Enabled = plugin.Settings.CoverBadges = true;
                foreach (var mode in new[]
                {
                    ApplicationMode.Desktop,
                    ApplicationMode.Fullscreen
                }

                )
                {
                    calls.Mode = mode;
                    var widget = (CoverBadgeControl)plugin.GetGameViewControl(new GetGameViewControlArgs { Name = "Badge" });
                    widget.GameContext = CoverGame(550);
                    widget.Refresh();
                    Eq(Visibility.Visible, widget.Visibility);
                    Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)widget.Content));
                    widget.Shutdown();
                }

                late = (Action<SteamCatalogSnapshot>)changed.GetValue(plugin.SteamCatalog);
                late(CoverCatalog("550:4:-1,551:4:-1,552:4:-1", "0", "0"));
                Eq(0, plugin.CoverHistory.PendingAppIds.Count);
            }

            late(CoverCatalog()); // a callback already captured before shutdown cannot restore flags
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
                Eq(0, plugin.CoverHistory.PendingAppIds.Count);
            Eq(0, calls.Starts);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
