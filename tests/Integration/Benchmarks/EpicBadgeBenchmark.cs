using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Launchers.Epic;

// Synthetic catalog lookups only; no launcher calls or UI rendering.
internal static class EpicBadgeBenchmark
{
    public static int Run()
    {
        AppDomain.MonitoringIsEnabled = true;
        var games = Enumerable.Range(0, 1000).Select(id => new Game {
            PluginId = EpicSnapshot.LibraryId, GameId = "app-" + id,
            Name = "Synthetic game"
        }).ToArray();
        var values = games.Select(game => new EpicProduct(game.GameId, "ns", "catalog",
            true, true, false, false, false, false)).ToArray();
        var elapsed = new double[5];
        var allocated = new long[5];
        int badges = 0;
        for (int sample = 0; sample < elapsed.Length; sample++)
        {
            var snapshot = new EpicSnapshot(DateTime.UtcNow, values, true, "Synthetic");
            foreach (var game in games) snapshot.Badge(game);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            var watch = Stopwatch.StartNew();
            for (int call = 0; call < 100000; call++)
                if (snapshot.Badge(games[call % games.Length]) != null) badges++;
            watch.Stop();
            if (!snapshot.Fresh) throw new InvalidOperationException("Benchmark snapshot expired");
            elapsed[sample] = watch.Elapsed.TotalMilliseconds;
            allocated[sample] = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        }
        Array.Sort(elapsed);
        Array.Sort(allocated);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "1000 idle Epic games; 100000 lookups; median of five: {0:F2} ms; {1} bytes allocated; badges={2}",
            elapsed[2], allocated[2], badges));
        return badges == 0 ? 0 : 1;
    }
}
