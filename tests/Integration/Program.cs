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
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--connection-restart-child") return ConnectionRestartChild(args[1]);
        if (args.Length == 2 && args[0] == "--reader-lifetime-child") return ReaderLifetimeChild(int.Parse(args[1]));
        if (args.Contains("--reader-lifetime-only"))
        {
            ReaderLifetimeTests();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--epic-badge-benchmark")) return EpicBadgeBenchmark.Run();
        if (args.Contains("--catalog-current-benchmark")) return CatalogCurrentBenchmark.Run();
        if (args.Contains("--idle-catalog-benchmark")) return IdleCatalogBenchmark.Run();
        if (args.Contains("--reader-resource-benchmark"))
        {
            int position = Array.IndexOf(args,"--reader-resource-benchmark");
            return ReaderResourceBenchmark.Run(uint.Parse(args[position+1]),args.Contains("--catalog-only"));
        }
        if (args.Contains("--pause-confirmation-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            PauseConfirmationTests();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--connection-management-only"))
        {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ConnectionManagementChecks();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--steam-ui-setup-only"))
        {
            Test("Steam UI setup recognizes existing markers and connection", SteamUiSetup);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--steam-ui-mapping-only"))
        {
            SteamUiMappingTests();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--background-progress-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            BackgroundProgressTests();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--shutdown-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            ExitCoreTests();
            PluginSmoke(args.Single(a => a.StartsWith("--playnite=")).Substring(11), args.Contains("--sdk-only"), false, false, false, true);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--ui-benchmark"))
            return LibraryBadgeBenchmark.Run();
        if (args.Contains("--library-benchmark"))
            return LibraryDiscoveryBenchmark.Run();
        if (args.Contains("--catalog-benchmark"))
            return CatalogLookupBenchmark.Run();
        if (args.Contains("--theme-ui-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            Test("shared status and original host bindings in both modes", UI);
            Test("adorner and root-grid surfaces", UniversalSurface);
            Test("close glyph under a custom theme", DismissStatus);
            Test("shared presenter completion cleanup", LifecycleUI);
            PluginSmoke(args.Single(a => a.StartsWith("--playnite=")).Substring(11), false, false, true);
            Console.WriteLine("RESULT " + passed + " passed; " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--resume-ui-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            PluginSmoke(args.Single(a => a.StartsWith("--playnite=")).Substring(11), false, false, false, true);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--cover-layer-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            Test("automatic cover actions", () => CoverLayeringChecks.Run(false, false));
            Test("nested automatic cover actions", () => CoverLayeringChecks.Run(false, true));
            Test("SDK cover actions", () => CoverLayeringChecks.Run(true, false));
            Test("nested SDK cover actions", () => CoverLayeringChecks.Run(true, true));
            Test("SDK sibling reordering", CoverLayeringChecks.MultipleSdkControls);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--cover-progress-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            Test("cover download percent and actual subsequent stage", CoverDownloadStages);
            Test("cover download/stage changes redraw cached artwork", CoverDownloadRendering);
            Test("automatic cover actions", () => CoverLayeringChecks.Run(false, false));
            Test("nested automatic cover actions", () => CoverLayeringChecks.Run(false, true));
            Test("SDK cover actions", () => CoverLayeringChecks.Run(true, false));
            Test("nested SDK cover actions", () => CoverLayeringChecks.Run(true, true));
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--badge-settings-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            PluginSmoke(args.Single(a => a.StartsWith("--playnite=")).Substring(11), false, false, true);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--close-review-only"))
        {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            PluginSmoke(args.Single(a => a.StartsWith("--playnite=")).Substring(11), true, false, false, false, false, true);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--handoff-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            Test("replacement removal", ReplacementRemoval);
            Test("replacement refusal and stop", ReplacementRemovalFailure);
            PluginSmoke(args.Single(a => a.StartsWith("--playnite=")).Substring(11), args.Contains("--sdk-only"), true);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        if (args.Contains("--library-rows-only"))
        {
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            Test("list indicators", LibraryRowBadges);
            Test("table indicators", LibraryTableBadges);
            Test("list adorner", LibraryRowAdorner);
            Test("virtualized list and cover switching", LibraryRowVirtualization);
            Test("GridView columns", LibraryGridViewBadges);
            Test("mixed covers, nested rows and SDK controls", LibraryMixedSurfaces);
            Test("cached library progress layouts", LibraryBadgeRenderingChecks.Run);
            Test("library indicators and two independent card meters", LibraryAndCardProgress);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        return RunIntegrationSuite(args);
    }
}
