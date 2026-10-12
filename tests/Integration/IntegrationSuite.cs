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
    private static int RunIntegrationSuite(string[] args)
    {
        Test("bounded foreground policy stops on acceptance and user app switch", ForegroundPolicy);
        Test("launcher registry supports independently registered providers", RegistryProviders);
        Test("universal status uses no theme anchors and preserves host content", UniversalSurface);
        Test("update commands require matching game, capability and live confirmation", ControlLifecycle);
        PauseConfirmationTests();
        CatalogTests();
        BackgroundProgressTests();
        RecoveryTests();
        Test("late command result cannot restore a cancelled session", ControlCancellation);
        Test("interactive card supports action clicks without blocking other theme controls", ControlSurface);
        Test("unverified frontend rejects update commands without loading Steam", ControlAbiRefusal);
        Test("native control refuses running, completed, stale and unknown update states", () =>
        {
            Eq(true, SteamControlPolicy.Available(1030, 33, 0, true));
            Eq(true, SteamControlPolicy.Available(518, 0, 4, true));
            Eq(false, SteamControlPolicy.Available(4, 33, 0, true));
            Eq(false, SteamControlPolicy.Available(1030 | 8192, 33, 0, true));
            Eq(false, SteamControlPolicy.Available(1030, 33, 0, false));
            Eq(false, SteamControlPolicy.Available(1030, 33 | 8, 0, true));
            Eq(false, SteamControlPolicy.Available(518, 0, 5, true));
            Eq(true, SteamControlPolicy.Available(1030, 0, 0, true));
        });
        Test("Steam download controls confirm global state rather than a queued game's pause", GlobalControlState);
        Test("global downloads metadata distinguishes old helper, active and paused", () =>
        {
            var frame = LiveFrame(550, 1030, 50, 100, 70, 100);
            Eq((bool?)null, SteamLiveSample.Parse(frame, 550).DownloadsEnabled);
            Eq((bool?)true, SteamLiveSample.Parse(AlterLive(frame, 0, BitConverter.GetBytes(1U)), 550).DownloadsEnabled);
            Eq((bool?)false, SteamLiveSample.Parse(AlterLive(frame, 0, BitConverter.GetBytes(2U)), 550).DownloadsEnabled);
            bool invalid = false;
            try
            {
                SteamLiveSample.Parse(AlterLive(frame, 0, BitConverter.GetBytes(3U)), 550);
            }
            catch (FormatException)
            {
                invalid = true;
            }

            Eq(true, invalid);
        });
        Test("download speed units convert bytes to bits with correct prefixes and culture", SpeedUnits);
        Test("Fullscreen close pauses before hiding, has no tooltip and remains visible with a custom theme button style", DismissStatus);
        Test("close failures and stale sessions cannot hide or pause a replacement launch", CloseFailures);
        Test("close waits for global downloads pause even when the selected game is paused", CloseGlobalState);
        Test("queue-aware close waits for both global pause and app removal", QueueAwareClose);
        Test("Pause and Resume retain queue membership and never send close", QueueAwarePauseResume);
        Test("queue removal confirmation rejects stale, cached, unknown and wrong-app evidence", QueueRemovalEvidence);
        Test("close acknowledgement survives completion and partial queue removal", QueueCloseCompletionRace);
        Test("queue close refusal is visible and requires an explicit retry", QueueCloseRefusal);
        Test("late queue-close acknowledgement cannot dismiss a replacement game", QueueCloseReplacement);
        Test("replacement removal shares fresh confirmation without dismissal or library navigation", ReplacementRemoval);
        Test("replacement removal rejection and shutdown cannot release a new Play", ReplacementRemovalFailure);
        Test("cover badges classify fresh imported Steam games and suppress optional/stale/unknown states", CoverBadgeStates);
        Test("cover download percent follows bytes and subsequent labels follow actual stage", CoverDownloadStages);
        Test("cover redraw follows download/stage changes while work percent remains equal", CoverDownloadRendering);
        Test("cover badges preserve artwork, templates and clicks while cleaning up on disable", CoverBadgeSurface);
        Test("cover badges handle scaled, clipped and recycled covers without leaking to another game", CoverBadgeReuse);
        Test("cover badges stay inside square UniformToFill tiles with portrait artwork", CoverBadgeCroppedPortrait);
        Test("SDK cover badge follows game context and suppresses its automatic duplicate", CoverBadgeSdk);
        Test("automatic cover artwork remains behind theme actions", () => CoverLayeringChecks.Run(false, false));
        Test("automatic cover layering follows wrapped images and dynamic action order", () => CoverLayeringChecks.Run(false, true));
        Test("SDK cover artwork remains behind theme actions", () => CoverLayeringChecks.Run(true, false));
        Test("SDK cover layering recognizes a wrapped adjacent image", () => CoverLayeringChecks.Run(true, true));
        Test("SDK siblings can reorder during automatic duplicate detection", CoverLayeringChecks.MultipleSdkControls);
        Test("list indicators reserve title space and retain Pause/Resume, identity, input and padding bindings", LibraryRowBadges);
        Test("reused list badge matches fresh rendering after resize, progress, font, contrast and culture changes", LibraryBadgeRenderingChecks.Run);
        Test("shared library download progress preserves two independent detailed-card meters", LibraryAndCardProgress);
        Test("table indicators stay in the name column across column resize and row recycling", LibraryTableBadges);
        Test("standard list templates use an adorner and clean up on view replacement", LibraryRowAdorner);
        Test("scrolling virtualized rows and switching back to covers cannot leak indicators", LibraryRowVirtualization);
        Test("GridView display-member columns identify the game field without title-string matching", LibraryGridViewBadges);
        Test("shared discovery preserves mixed surfaces, nested row identity, full covers, SDK suppression and cleanup", LibraryMixedSurfaces);
        Test("cover update history survives restart and reconciles completed Steam updates", CoverHistoryPersistence);
        Test("cover history retains unknown rows and rejects stale/corrupt/unwritable data safely", CoverHistoryUnavailable);
        SteamUiMappingTests();
        ReaderLifetimeTests();
        Test("Steam UI access requires explicit setup and preserves existing markers", SteamUiSetup);
        Test("automatic Steam folder discovery persists and replaces moved or invalid locations", SteamLocationPersistence);
        Test("automatic and SDK badges retain only Update Required when Steam is unavailable", CoverHistorySurfaces);
        Test("completion cannot flash Launching before two idle samples", () =>
        {
            var p = new SteamProgress();
            Eval(p, M(1286));
            var first = Eval(p, M(4, 100, 100));
            Eq(true, first.CompletionPending);
            Eq("Finalizing", first.Display.Stage);
            Eq((int?)null, first.Percent);
            Eval(p, M(1286));
            Eq(UpdatePhase.Updating, Eval(p, M(4, 100, 100)).Phase);
            Eq(UpdatePhase.Ready, Eval(p, M(4, 100, 100)).Phase);
        });
        Test("monitor/update/control cleanup after realistic complete/running sequence", LifecycleUI);
        Test("monitor carries independent live display to UI", () =>
        {
            using (var monitor = new UpdateMonitor((s, d) =>
            {
            }))
            {
                var session = new DisplaySession();
                monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, session, 500);
                for (int i = 0; i < 30 && monitor.Current.Display == null; i++)
                    Thread.Sleep(20);
                Eq(session.Display, monitor.Current.Display);
                Eq((int?)93, monitor.Current.Display.WorkPercent);
            }
        });
        Test("malformed unclosed string", () => Bad("AppState { appid \"550 }"));
        Test("malformed unclosed object", () => Bad("AppState { appid 550"));
        Test("negative number", () => Bad("AppState { appid 550 StateFlags -1 }"));
        Test("duplicate relevant field", () => Bad("AppState { appid 550 StateFlags 4 StateFlags 1286 }"));
        Test("wrong appid", () => Bad("AppState { appid 551 }"));
        Test("numeric overflow", () => Bad("AppState { appid 550 BytesToDownload 18446744073709551616 }"));
        Test("nested depots do not shadow top-level values", () => Eq((ulong?)4, SteamManifestParser.Parse("// comment\nAppState { appid 550 StateFlags 4 InstalledDepots { x { StateFlags 1286 } } }", 550).Flags));
        Test("escaped path in libraryfolders", () =>
        {
            var paths = SteamLocator.ParseLibraries(@"C:\Steam", "libraryfolders { 0 { path \"C:\\\\Steam\" apps { 550 100 } } 1 { path \"D:\\\\Games\\\\SteamLibrary\" } }");
            Eq(2, paths.Count);
            Eq(@"D:\Games\SteamLibrary", paths[1]);
        });
        Test("legacy secondary libraries", () => Eq(2, SteamLocator.ParseLibraries(@"C:\Steam", "libraryfolders { 0 \"C:\\\\Steam\" 1 \"D:\\\\SteamLibrary\" }").Count));
        Test("invalid library rows cannot hide valid absolute libraries", InvalidLibraryRows);
        Test("partial library metadata preserves primary library discovery", PartialLibraryMetadata);
        Test("unreadable duplicate manifest cannot hide another valid library copy", DuplicateManifestRecovery);
        Test("actual secondary library manifest discovery on synthetic paths", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "UpdateStatusTests-" + Guid.NewGuid());
            string secondary = Path.Combine(root, "Secondary");
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            Directory.CreateDirectory(Path.Combine(secondary, "steamapps"));
            try
            {
                File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), "libraryfolders { 0 { path \"" + root.Replace("\\", "\\\\") + "\" } 1 { path \"" + secondary.Replace("\\", "\\\\") + "\" } }");
                string path = Path.Combine(secondary, "steamapps", "appmanifest_550.acf");
                File.WriteAllText(path, Fixture(4, 0, 0, 0, 0));
                Eq(path, new SteamLocator().FindManifest(root, 550, null));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });
        Test("fresh log only, scoped appid and completion", () =>
        {
            var log = new SteamContentLog(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()), 550);
            log.Apply("AppID 5500 update started : download 0/100");
            Eq(false, log.Active);
            log.Apply("AppID 550 update started : download 0/100");
            Eq(true, log.Active);
            log.Apply("AppID 550 finished update");
            Eq(true, log.Active); // Commit still active.
            log.Apply("AppID 550 App update changed : None");
            Eq(false, log.Active);
        });
        Test("ownership errors do not indicate update failure", () =>
        {
            var log = new SteamContentLog("missing", 550);
            log.Apply("AppID 550 failed to update ownership ticket (Access Denied)");
            Eq((string)null, log.Error);
        });
        Test("historical log is ignored; partial fresh lines join", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "UpdateStatusLog-" + Guid.NewGuid());
            try
            {
                File.WriteAllText(path, "AppID 550 update started : old\n");
                var log = new SteamContentLog(path, 550);
                log.Poll();
                Eq(false, log.Active);
                File.AppendAllText(path, "AppID 550 App update changed : Running");
                log.Poll();
                Eq(false, log.Active);
                File.AppendAllText(path, " Update,Downloading,\n");
                log.Poll();
                Eq(true, log.Active);
                File.AppendAllText(path, "AppID 550 scheduler finished : result Disk Write Failure\n");
                log.Poll();
                Eq(false, log.Active);
                Eq(true, log.Error != null);
            }
            finally
            {
                File.Delete(path);
            }
        });
        Test("steam imported identity uses plugin guid and gameid", () =>
        {
            var g = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Incorrect name",
                PluginId = SteamGameIdentity.LibraryId,
                GameId = "550"
            };
            Eq("550", SteamGameIdentity.Resolve(g).LauncherId);
        });
        Test("unrelated launcher numeric gameid is ignored", () => Eq((LaunchIdentity)null, SteamGameIdentity.Resolve(new Game { GameId = "550", Name = "Left 4 Dead 2" })));
        Test("explicit play uri", () => Eq("550", SteamGameIdentity.Resolve(new Game { GameActions = new ObservableCollection<GameAction> { new GameAction { IsPlayAction = true, Path = "steam://rungameid/550" } } }).LauncherId));
        Test("conflicting manual play URIs ignored", () => Eq((LaunchIdentity)null, SteamGameIdentity.Resolve(new Game { GameActions = new ObservableCollection<GameAction> { new GameAction { IsPlayAction = true, Path = "steam://run/550" }, new GameAction { IsPlayAction = true, Path = "steam://run/551" } } })));
        Test("steam.exe applaunch action", () => Eq("550", SteamGameIdentity.Resolve(new Game { GameActions = new ObservableCollection<GameAction> { new GameAction { IsPlayAction = true, Path = @"C:\Steam\steam.exe", Arguments = "-applaunch 550 -novid" } } }).LauncherId));
        Test("monitor cancellation ignores in-flight results", () =>
        {
            using (var monitor = new UpdateMonitor((s, d) =>
            {
            }))
            {
                var blocking = new BlockingSession();
                Guid id = Guid.NewGuid();
                monitor.Start(new LaunchIdentity { GameId = id, LauncherId = "550" }, blocking, 500);
                if (!blocking.Entered.WaitOne(2000))
                    throw new Exception("Poll did not start");
                monitor.Stop(id);
                blocking.Release.Set();
                Thread.Sleep(150);
                Eq(UpdatePhase.Idle, monitor.Current.Phase);
            }
        });
        Test("unrelated game stop doesn't cancel monitored game", () =>
        {
            using (var monitor = new UpdateMonitor((s, d) =>
            {
            }))
            {
                monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, new FixedSession(), 1000);
                monitor.Stop(Guid.NewGuid());
                Eq(true, monitor.Current.Visible);
            }
        });
        Test("catalog lookup preserves sorted entries, first duplicate and independent snapshots", () =>
        {
            var snapshot = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(551, "Second"), new SteamCatalogApp(550, "First"), new SteamCatalogApp(550, "Duplicate") }, null, "Offline");
            Eq((uint)550, snapshot.Entries[0].AppId);
            Eq((uint)551, snapshot.Entries[2].AppId);
            Eq(snapshot.Entries[0], snapshot.Find(550));
            Eq((SteamCatalogEntry)null, snapshot.Find(999));
            var replacement = new SteamCatalogSnapshot(new[] { new SteamCatalogApp(551, "Replacement") }, null, "Offline");
            Eq((SteamCatalogEntry)null, replacement.Find(550));
            Eq(snapshot.Entries[0], snapshot.Find(550));
            bool readOnly = false;
            try
            {
                snapshot.Entries.Clear();
            }
            catch (NotSupportedException)
            {
                readOnly = true;
            }

            Eq(true, readOnly);
        });
        Test("Theme-independent status preserves host bindings and cleans up in both modes", UI);
        Test("basic error logging remains without verbose progress traces", BasicErrorLogging);
        string playniteArg = args.FirstOrDefault(a => a.StartsWith("--playnite="));
        if (playniteArg != null)
        {
            Test("plugin constructor, settings serialization and SDK lifecycle smoke", () => PluginSmoke(playniteArg.Substring(11), args.Contains("--sdk-only")));
        }

        if (args.Contains("--local-steam"))
            Test("all real local manifests read-only", () =>
            {
                var locator = new SteamLocator();
                string root = locator.FindRoot();
                if (root == null)
                    throw new Exception("Steam not detected");
                int count = 0;
                foreach (string library in locator.Libraries(root))
                    foreach (string path in Directory.GetFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
                    {
                        var m = SteamManifestParser.Parse(SteamManifestParser.ReadShared(path), 0);
                        if (m.AppId == 0)
                            throw new Exception("Bad AppID");
                        count++;
                    }

                Console.WriteLine("Read-only parsed " + count + " real manifests");
                if (count == 0)
                    throw new Exception("No manifests found");
            });
        if (args.Contains("--native-steam"))
            Test("real isolated Steam IPC reader and cleanup", () =>
            {
                var root = new SteamLocator().FindRoot();
                if (root == null)
                    throw new Exception("Steam not found");
                using (var reader = new SteamLiveReader(root, 252490, (s, d) => Console.WriteLine(s)))
                {
                    SteamLiveSample sample = null;
                    for (int i = 0; i < 60 && sample == null; i++)
                    {
                        sample = reader.TryRead();
                        if (sample == null)
                            Thread.Sleep(100);
                    }

                    if (sample == null)
                        throw new Exception("No live IPC reply in six seconds");
                    Eq((uint)252490, sample.AppId);
                    Eq(true, sample.DownloadsEnabled.HasValue);
                    Console.WriteLine("Native IPC counters " + sample.Downloaded + "/" + sample.DownloadTotal + "; " + sample.Staged + "/" + sample.StageTotal + "; downloadsEnabled=" + sample.DownloadsEnabled + "; NETWORK=" + sample.NetworkBytesPerSecond + "; activeAppId=" + sample.NetworkAppId);
                }
            });
        if (args.Contains("--native-steam"))
            Test("real Steam catalog getters, background lifecycle and cleanup", NativeCatalog);
        if (args.Contains("--native-steam"))
            Test("unverified Steam DLL rejected before native loading", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "UpdateStatusAbi-" + Guid.NewGuid());
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "steamclient64.dll");
                File.WriteAllText(path, "unverified test fixture");
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamUpdateReader.exe"), "\"" + root + "\" 252490 1")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    using (var child = System.Diagnostics.Process.Start(info))
                    {
                        if (!child.WaitForExit(5000))
                        {
                            child.Kill();
                            throw new Exception("ABI refusal timed out");
                        }

                        string error = child.StandardError.ReadToEnd();
                        Eq(1, child.ExitCode);
                        Eq(true, error.Contains("Steam UI connection unavailable"));
                    }
                }
                finally
                {
                    File.Delete(path);
                    Directory.Delete(root);
                }
            });
        Console.WriteLine("RESULT " + passed + " passed; " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
