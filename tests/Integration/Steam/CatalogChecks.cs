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
    private static SteamQueueSample CatalogFrame(string rows, uint[] ids, string enabled = "1", string active = "0", string count = "0", uint sequence = 1)
    {
        var sample = SteamQueueSample.Parse("Q3|" + enabled + "|" + active + "|" + count + "|" + sequence + "|" + rows + "|-", ids);
        sample.ReceivedUtc = DateTime.UtcNow;
        return sample;
    }

    private static void NativeCatalog()
    {
        using (var catalog = new SteamUpdateCatalog((s, d) =>
        {
            if (!d)
                Console.WriteLine(s);
        }))
        {
            catalog.Start();
            Until(() => catalog.Current.Live);
            var snapshot = catalog.Current;
            Eq(true, snapshot.Entries.Count > 0);
            Eq(true, snapshot.DownloadsEnabled.HasValue);
            foreach (var entry in snapshot.Entries)
                Console.WriteLine("Catalog AppID=" + entry.AppId + " status=" + entry.Status + " requiresUpdate=" + entry.RequiresUpdate + " queueIndex=" + entry.QueueIndex);
            catalog.Stop();
            Eq(false, catalog.Current.Live);
            Eq(true, catalog.Current.Entries.All(entry => !entry.RequiresUpdate.HasValue));
        }
    }

    private static void CatalogTests()
    {
        Test("optional updates and repair flags are not labelled up-to-date", () =>
        {
            var sample = CatalogFrame("550:20:-1,551:36:-1", new uint[] { 550, 551 });
            var snapshot = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "Optional"), new SteamCatalogApp(551, "Repair") }, sample, null);
            Eq(SteamCatalogStatus.UpdateAvailable, snapshot.Find(550).Status);
            Eq((bool?)false, snapshot.Find(550).RequiresUpdate);
            Eq(SteamCatalogStatus.RepairRequired, snapshot.Find(551).Status);
        });
        Test("catalog separates up-to-date, required, paused and running states", () =>
        {
            var sample = CatalogFrame("550:4:-1,551:6:-1,552:1542:-1,553:8196:-1", new uint[] { 550, 551, 552, 553 });
            var snapshot = new SteamCatalogSnapshot(sample.Items.Select(item => new SteamCatalogApp(item.AppId, "fixture")), sample, null);
            Eq(SteamCatalogStatus.UpToDate, snapshot.Find(550).Status);
            Eq((bool?)false, snapshot.Find(550).RequiresUpdate);
            Eq(SteamCatalogStatus.RequiresUpdate, snapshot.Find(551).Status);
            Eq((bool?)true, snapshot.Find(551).RequiresUpdate);
            Eq(SteamCatalogStatus.Paused, snapshot.Find(552).Status);
            Eq(true, snapshot.Find(553).IsRunning);
        });
        Test("catalog reads native queue order independently of the chosen game", () =>
        {
            var sample = CatalogFrame("550:14:1,551:1294:0", new uint[] { 550, 551 }, "1", "551", "2");
            var snapshot = new SteamCatalogSnapshot(sample.Items.Select(item => new SteamCatalogApp(item.AppId, "fixture")), sample, null);
            Eq(SteamCatalogStatus.Queued, snapshot.Find(550).Status);
            Eq((int?)1, snapshot.Find(550).QueueIndex);
            Eq(SteamCatalogStatus.Updating, snapshot.Find(551).Status);
            Eq((int?)0, snapshot.Find(551).QueueIndex);
            Eq((uint?)551, snapshot.ActiveAppId);
        });
        Test("global catalog pause does not mark an updated game paused", () =>
        {
            var sample = CatalogFrame("550:14:0,551:4:-1", new uint[] { 550, 551 }, "0", "0", "1");
            var snapshot = new SteamCatalogSnapshot(sample.Items.Select(item => new SteamCatalogApp(item.AppId, "fixture")), sample, null);
            Eq(SteamCatalogStatus.Paused, snapshot.Find(550).Status);
            Eq(SteamCatalogStatus.UpToDate, snapshot.Find(551).Status);
        });
        Test("catalog unknown flags and racing reads cannot become Requires Update", () =>
        {
            var sample = CatalogFrame("550:-:-,551:2147483652:-1,552:0:-1", new uint[] { 550, 551, 552 });
            var snapshot = new SteamCatalogSnapshot(sample.Items.Select(item => new SteamCatalogApp(item.AppId, "fixture")), sample, null);
            Eq(true, snapshot.Entries.All(entry => entry.Status == SteamCatalogStatus.Unknown && !entry.RequiresUpdate.HasValue));
        });
        Test("catalog inconsistent globals suppress active/queue position and pause", () =>
        {
            var sample = CatalogFrame("550:14:0", new uint[] { 550 }, "-", "-", "-");
            var snapshot = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture") }, sample, null);
            Eq((bool?)null, snapshot.DownloadsEnabled);
            Eq((uint?)null, snapshot.QueueCount);
            Eq((int?)null, snapshot.Find(550).QueueIndex);
            Eq(SteamCatalogStatus.Queued, snapshot.Find(550).Status);
        });
        Test("queue reordering with unchanged count suppresses mixed positions", () =>
        {
            var sample = CatalogFrame("550:14:0,551:14:0", new uint[] { 550, 551 }, "1", "0", "2");
            var snapshot = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "First"), new SteamCatalogApp(551, "Second") }, sample, null);
            Eq(true, snapshot.Entries.All(entry => entry.RequiresUpdate == true && !entry.QueueIndex.HasValue));
        });
        Test("catalog empty queue and out-of-range indices cannot invent membership", () =>
        {
            foreach (string count in new[]
            {
                "0",
                "1"
            }

            )
            {
                var sample = CatalogFrame("550:6:9", new uint[] { 550 }, "1", "0", count);
                var snapshot = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture") }, sample, null);
                Eq((int?)null, snapshot.Find(550).QueueIndex);
                Eq(SteamCatalogStatus.RequiresUpdate, snapshot.Find(550).Status);
            }
        });
        Test("catalog protocol refuses partial, foreign, duplicate and malformed identities", () =>
        {
            foreach (string line in new[]
            {
                "Q3|1|0|1|1|550:4:-1|-",
                "Q3|1|0|1|1|550:4:-1,550:4:-1|-",
                "Q3|1|0|1|1|550:4:-1,552:4:-1|-",
                "Q3|1|0|1|0|550:4:-1,551:4:-1|-",
                "Q3|2|0|1|1|550:4:-1,551:4:-1|-",
                "Q3|-|0|-|1|550:4:-1,551:4:-1|-",
                "Q3|1|0|1|1|550:-:0,551:4:-1|-",
                "Q3|1|0|1|1|550:4:-2,551:4:-1|-",
                "Q3|1|0|1|1|550:4:2147483648,551:4:-1|-",
                "Q3|1|0|100001|1|550:4:-1,551:4:-1|-",
                "S4|550|4|-"
            }

            )
            {
                bool refused = false;
                try
                {
                    SteamQueueSample.Parse(line, new uint[] { 550, 551 });
                }
                catch (FormatException)
                {
                    refused = true;
                }

                Eq(true, refused);
            }

            Eq(0, SteamQueueSample.Parse("Q3|1|0|0|1|-|-", new uint[0]).Items.Count);
        });
        Test("catalog discovery visits libraries, deduplicates and skips wrong identities", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "playnite-catalog-" + Guid.NewGuid());
            var folder = Path.Combine(root, "steamapps");
            var other = Path.Combine(root, "other");
            var otherApps = Path.Combine(other, "steamapps");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(otherApps);
            try
            {
                File.WriteAllText(Path.Combine(folder, "libraryfolders.vdf"), "libraryfolders { 0 { path \"" + root.Replace("\\", "\\\\") + "\" } 1 { path \"" + other.Replace("\\", "\\\\") + "\" } }");
                File.WriteAllText(Path.Combine(folder, "appmanifest_550.acf"), Fixture(4, 100, 100, 0, 0).Replace("\"StateFlags\"", "\"name\" \"Readable name\" \"StateFlags\""));
                File.WriteAllText(Path.Combine(otherApps, "appmanifest_550.acf"), Fixture(6, 100, 0, 0, 0));
                File.WriteAllText(Path.Combine(otherApps, "appmanifest_551.acf"), Fixture(6, 100, 0, 0, 0).Replace("\"550\"", "\"551\""));
                File.WriteAllText(Path.Combine(folder, "appmanifest_999.acf"), Fixture(6, 100, 0, 0, 0));
                var apps = SteamCatalogDiscovery.Read(root, (s, d) =>
                {
                });
                Eq(2, apps.Count);
                Eq("Readable name", apps.Single(app => app.AppId == 550).Name);
            }
            finally
            {
                var resolved = Path.GetFullPath(root);
                var expectedPrefix = Path.Combine(Path.GetTempPath(), "playnite-catalog-");
                if (!resolved.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Unsafe fixture cleanup path");
                Directory.Delete(resolved, true);
            }
        });
        Test("background catalog detects Steam startup, refreshes all apps and resets on exit", CatalogLifecycle);
        Test("late catalog discovery cannot repopulate a stopped service", () =>
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var finished = new ManualResetEvent(false))
            using (var catalog = new SteamUpdateCatalog(() => "session", () =>
            {
                entered.Set();
                release.WaitOne(4000);
                finished.Set();
                return new List<SteamCatalogApp>
                {
                    new SteamCatalogApp(550, "fixture")
                };
            }, apps =>
            {
                throw new Exception("Late reader opened");
            }, (s, d) =>
            {
            }))
            {
                catalog.Start();
                Eq(true, entered.WaitOne(2000));
                catalog.Stop();
                release.Set();
                Eq(true, finished.WaitOne(2000));
                Thread.Sleep(150);
                Eq(false, catalog.Current.Live);
                Eq(0, catalog.Current.Entries.Count);
            }
        });
    }

    private static void PluginCatalogLifecycle()
    {
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI)).GetTransparentProxy();
        var readers = new List<QueueFixture>();
        var catalog = new SteamUpdateCatalog(() => "fixture-session", () => new[] { new SteamCatalogApp(550, "fixture") }, apps =>
        {
            var reader = new QueueFixture
            {
                Sample = CatalogFrame("550:6:-1", new uint[] { 550 })
            };
            readers.Add(reader);
            return reader;
        }, (s, d) =>
        {
        });
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        {
            plugin.SteamCatalog.Dispose();
            typeof(PlayniteUpdateStatus.Plugin).GetProperty("SteamCatalog").SetValue(plugin, catalog, null);
            plugin.Settings.Enabled = true;
            plugin.OnApplicationStarted(new OnApplicationStartedEventArgs());
            Until(() => catalog.Current.Live);
            var game = new Game
            {
                Id = Guid.NewGuid()
            };
            plugin.OnGameStopped(Event<OnGameStoppedEventArgs>(game));
            Eq(true, catalog.Current.Live);
            plugin.Settings.Enabled = false;
            plugin.ApplySettings();
            Eq(false, catalog.Current.Live);
            Eq(true, readers.Last().Disposed);
            plugin.Settings.Enabled = true;
            plugin.ApplySettings();
            Until(() => catalog.Current.Live && readers.Count == 2);
            plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
            Eq(false, catalog.Current.Live);
            Eq(true, readers.Last().Disposed);
        }
    }
}
