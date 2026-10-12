using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using PlayniteUpdateStatus.UI;
using PlayniteUpdateStatus.Core;

// Measures only badge configuration, with no Steam calls or visible windows.
internal static class LibraryBadgeBenchmark
{
    public static int Run()
    {
        new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        AppDomain.MonitoringIsEnabled = true;
        Console.WriteLine("Badge configuration microbenchmark; CLR " + Environment.Version + "; 64-bit=" + Environment.Is64BitProcess);
        Console.WriteLine("300 badges, 100 passes, five samples; median elapsed time and managed allocation.");
        Measure("unchanged", false, false);
        Measure("width changes", true, false);
        Measure("progress changes", false, true);
        return 0;
    }

    private static void Measure(string name, bool resize, bool progress)
    {
        var badges = Enumerable.Range(0, 300).Select(i => new LibraryRowBadgeVisual()).ToArray();
        var states = Enumerable.Range(0, 100).Select(i => new CoverBadgeInfo(Guid.Empty, "Updating", 93, new UpdateDisplay { Downloaded = (ulong)i, DownloadTotal = 100, WorkPercent = 93, Stage = "Updating files" })).ToArray();
        var milliseconds = new double[5];
        var allocated = new long[5];
        double checksum = 0;
        foreach (var badge in badges)
            badge.Configure(states[65], 16, 160);
        for (int sample = 0; sample < milliseconds.Length; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            var watch = Stopwatch.StartNew();
            for (int pass = 0; pass < 100; pass++)
                foreach (var badge in badges)
                    checksum += badge.Configure(states[progress ? pass : 65], 16, resize ? (pass % 2 == 0 ? 160 : 55) : 160);
            watch.Stop();
            milliseconds[sample] = watch.Elapsed.TotalMilliseconds;
            allocated[sample] = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        }

        Array.Sort(milliseconds);
        Array.Sort(allocated);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1:F2} ms; {2:F2} MiB allocated; checksum={3:F2}", name, milliseconds[2], allocated[2] / 1048576.0, checksum));
    }
}
