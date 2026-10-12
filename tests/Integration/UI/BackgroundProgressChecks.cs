using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

internal static partial class Program
{
    private static string BackgroundPayload(ulong downloaded = 62, uint flags = 1294, uint state = 97, uint appId = 550)
    {
        var sample = SteamJson.Decode<SteamLiveSample>(LiveFrame(appId, flags, downloaded, 100, 93, 100).Split('|')[3]);
        sample.UpdateState = state;
        sample.DownloadsEnabled = true;
        return appId + ":" + flags + ":" + sample.Payload();
    }

    private static SteamQueueSample BackgroundSample(string payload, string enabled = "1", string active = "550", string rows = "550:1294:0,551:14:1")
    {
        var sample = SteamQueueSample.Parse("Q3|" + enabled + "|" + active + "|2|1|" + rows + "|" + payload, new uint[] { 550, 551 });
        sample.ReceivedUtc = DateTime.UtcNow;
        return sample;
    }

    private static SteamCatalogSnapshot BackgroundSnapshot(SteamQueueSample sample)
    {
        return new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "Already downloading"), new SteamCatalogApp(551, "Queued") }, sample, null);
    }

    private static void BackgroundProgressTests()
    {
        Test("cold Playnite library shows download bytes without a Play session", () =>
        {
            var catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload()));
            using (var monitor = new UpdateMonitor((s, w) =>
            {
            }))
            {
                var badge = new CoverBadgeSnapshot(catalog, null, monitor.Current).Find(CoverGame(550));
                Eq("Downloading", badge.CoverText);
                Eq((int?)62, badge.CoverPercent);
                Eq(UpdatePhase.Idle, monitor.Current.Phase);
                Eq("Update Required", new CoverBadgeSnapshot(catalog).Find(CoverGame(551)).CoverText);
            }
        });
        Test("background progress advances independently of selected Play", () =>
        {
            var first = BackgroundSnapshot(BackgroundSample(BackgroundPayload(62)));
            var next = BackgroundSnapshot(BackgroundSample(BackgroundPayload(80)));
            var game = CoverGame(550);
            Eq((int?)62, new CoverBadgeSnapshot(first).Find(game).CoverPercent);
            Eq((int?)80, new CoverBadgeSnapshot(next).Find(game).CoverPercent);
        });
        Test("background work percentage is never a total update percentage", () =>
        {
            var catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload(20)));
            var info = new CoverBadgeSnapshot(catalog).Find(CoverGame(550));
            Eq("Downloading", info.CoverText);
            Eq((int?)20, info.CoverPercent);
        });
        Test("completed payload switches to real work stage without a percentage", () =>
        {
            foreach (var state in new uint[]
            {
                129,
                257,
                513
            }

            )
            {
                var catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload(100, 1294, state)));
                var info = new CoverBadgeSnapshot(catalog).Find(CoverGame(550));
                Eq(state == 129 ? "Verifying files" : state == 257 ? "Installing files" : "Finalizing", info.CoverText);
                Eq((int?)null, info.CoverPercent);
            }
        });
        Test("selected update retains priority over background measurements", () =>
        {
            var game = CoverGame(550);
            var catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload(80)));
            var selected = new UpdateState
            {
                GameId = game.Id,
                Phase = UpdatePhase.Updating,
                ControlPaused = false,
                Display = new UpdateDisplay
                {
                    Downloaded = 20,
                    DownloadTotal = 100
                }
            };
            Eq((int?)20, new CoverBadgeSnapshot(catalog, null, selected).Find(game).CoverPercent);
            selected.ControlPaused = true;
            Eq("Paused", new CoverBadgeSnapshot(catalog, null, selected).Find(game).CoverText);
            selected.Phase = UpdatePhase.Ready;
            Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(catalog, null, selected).Find(game));
        });
        Test("queued, paused and completed background apps do not borrow progress", () =>
        {
            var paused = BackgroundSnapshot(BackgroundSample("-", "0", "0", "550:1542:-1,551:14:0"));
            Eq("Update Required", new CoverBadgeSnapshot(paused).Find(CoverGame(550)).CoverText);
            var ready = BackgroundSnapshot(BackgroundSample("-", "1", "0", "550:4:-1,551:14:0"));
            Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(ready).Find(CoverGame(550)));
        });
        Test("old Q1 and missing Q2 measurements preserve generic Updating", () =>
        {
            foreach (var sample in new[]
            {
                CatalogFrame("550:1294:0,551:14:1", new uint[] { 550, 551 }, "1", "550", "2"),
                BackgroundSample("-")
            }

            )
            {
                var badge = new CoverBadgeSnapshot(BackgroundSnapshot(sample)).Find(CoverGame(550));
                Eq("Updating", badge.CoverText);
                Eq((int?)null, badge.CoverPercent);
            }
        });
        Test("malformed optional counters cannot erase valid queue state", () =>
        {
            foreach (string payload in new[]
            {
                "550:1294:00",
                "bad:frame",
                "550:1294:" + new string ('Z', 496),
                BackgroundPayload(101)
            }

            )
            {
                var sample = BackgroundSample(payload);
                Eq((SteamLiveSample)null, sample.ActiveUpdate);
                var catalog = BackgroundSnapshot(sample);
                Eq(SteamCatalogStatus.Updating, catalog.Find(550).Status);
                Eq("Updating", new CoverBadgeSnapshot(catalog).Find(CoverGame(550)).CoverText);
            }
        });
        Test("wrong app, changed flags and mismatched pause cannot authorize counters", () =>
        {
            foreach (string payload in new[]
            {
                BackgroundPayload(62, 1294, 97, 551),
                BackgroundPayload(62, 1030),
                PausedBackgroundPayload()
            }

            )
            {
                var sample = BackgroundSample(payload);
                Eq((SteamLiveSample)null, sample.ActiveUpdate);
            }
        });
        Test("unknown update-stage bits suppress numeric background progress", () =>
        {
            var catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload(62, 1294, 2048)));
            var info = new CoverBadgeSnapshot(catalog).Find(CoverGame(550));
            Eq("Updating", info.CoverText);
            Eq((int?)null, info.CoverPercent);
        });
        Test("stale and future catalog receipts do not supply download percentages", () =>
        {
            foreach (int seconds in new[]
            {
                -10,
                10
            }

            )
            {
                var sample = BackgroundSample(BackgroundPayload());
                sample.ReceivedUtc = DateTime.UtcNow.AddSeconds(seconds);
                var catalog = BackgroundSnapshot(sample);
                Eq((UpdateDisplay)null, catalog.DisplayForActiveApp(550));
                Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(catalog).Find(CoverGame(550)));
            }
        });
        Test("background snapshot isolates reader and display mutation", () =>
        {
            var sample = BackgroundSample(BackgroundPayload());
            var catalog = BackgroundSnapshot(sample);
            sample.ActiveUpdate.Downloaded = 99;
            var display = catalog.DisplayForActiveApp(550);
            Eq((ulong?)62, display.Downloaded);
            display.Downloaded = 1;
            Eq((ulong?)62, catalog.DisplayForActiveApp(550).Downloaded);
            Eq((UpdateDisplay)null, catalog.DisplayForActiveApp(551));
        });
        Test("active app handoff cannot assign previous bytes to the next game", () =>
        {
            var catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload(), "1", "551", "550:14:1,551:1294:0"));
            Eq("Update Required", new CoverBadgeSnapshot(catalog).Find(CoverGame(550)).CoverText);
            var next = new CoverBadgeSnapshot(catalog).Find(CoverGame(551));
            Eq("Updating", next.CoverText);
            Eq((int?)null, next.CoverPercent);
        });
        Test("shared native payload omits padding and preserves reviewed counters", BackgroundPayloadSafety);
        foreach (bool fullscreen in new[]
        {
            false,
            true
        }

        )
        {
            bool selected = fullscreen;
            Test("external update renders in " + (fullscreen ? "Fullscreen" : "Desktop") + " cover/list and SDK surfaces", () => BackgroundProgressUI(selected));
        }
    }

    private static string PausedBackgroundPayload()
    {
        var sample = SteamJson.Decode<SteamLiveSample>(BackgroundPayload().Split(':')[2]);
        sample.DownloadsEnabled = false;
        return "550:1294:" + sample.Payload();
    }

    private static void BackgroundPayloadSafety()
    {
        var sample = SteamJson.Decode<SteamLiveSample>(BackgroundPayload().Split(':')[2]);
        var live = SteamLiveSample.Parse(sample.Frame(), 550);
        Eq(62ul, live.Downloaded);
        Eq(100ul, live.DownloadTotal);
        Eq(93ul, live.Staged);
        Eq((bool?)true, live.DownloadsEnabled);
        Eq((uint?)null, live.NetworkBytesPerSecond);
        sample.NetworkBytesPerSecond = 1024;
        sample.NetworkAppId = 550;
        sample.DownloadsEnabled = false;
        live = SteamLiveSample.Parse(sample.Frame(), 550);
        Eq((uint?)1024, live.NetworkBytesPerSecond);
        Eq((bool?)false, live.DownloadsEnabled);
        sample.AppId = 551;
        BadLive("S4|550|1294|" + sample.Payload());
    }

    private static void BackgroundProgressUI(bool fullscreen)
    {
        if (Application.Current == null)
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        var game = CoverGame(550);
        Image cover;
        var tile = CoverTile(game, out cover);
        var list = new ListBox
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        TextBlock title;
        var row = LibraryRow(game, out title, false);
        list.Items.Add(row);
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(tile);
        var window = new TestWindow
        {
            Width = fullscreen ? 800 : 650,
            Height = 400,
            Content = root
        };
        var catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload()));
        var sdk = new CoverBadgeControl(() => catalog, () => true)
        {
            GameContext = game
        };
        using (var presenter = new CoverBadgePresenter((s, w) =>
        {
        }))
        {
            try
            {
                window.Show();
                window.UpdateLayout();
                presenter.Refresh(window, catalog, true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                Eq(0, presenter.ActiveRowCount);
                var coverBadge = Visuals<CoverBadgeVisual>(tile).Single();
                Eq("Downloading — 62%", System.Windows.Automation.AutomationProperties.GetName(coverBadge));
                sdk.Refresh();
                Eq("Downloading — 62%", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload(100, 1294, 129)));
                presenter.Refresh(window, catalog, true);
                window.UpdateLayout();
                sdk.Refresh();
                Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName(coverBadge));
                Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                root.Children.Clear();
                root.Children.Add(list);
                Grid.SetColumnSpan(list, 2);
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                window.UpdateLayout();
                presenter.Refresh(window, catalog, true);
                window.UpdateLayout();
                Eq(0, presenter.ActiveCount);
                Eq(1, presenter.ActiveRowCount);
                var rowBadge = Visuals<LibraryRowBadgeVisual>(row).Single();
                Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName(rowBadge));
                catalog = BackgroundSnapshot(BackgroundSample(BackgroundPayload()));
                presenter.Refresh(window, catalog, true);
                window.UpdateLayout();
                Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(rowBadge));
            }
            finally
            {
                sdk.Shutdown();
                window.Close();
            }
        }
    }
}
