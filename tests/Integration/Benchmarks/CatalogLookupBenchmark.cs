using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using PlayniteUpdateStatus.Launchers.Steam;

// Repeated identity lookups against one immutable catalog, without Steam calls.
internal static class CatalogLookupBenchmark
{
    public static int Run()
    {
        AppDomain.MonitoringIsEnabled = true;
        var catalog = new SteamCatalogSnapshot(Enumerable.Range(1, 10000).Reverse().Select(id => new SteamCatalogApp((uint)id, "Synthetic game")), null, "Synthetic offline catalog");
        var elapsed = new double[5];
        var allocated = new long[5];
        long checksum = 0;
        for (uint id = 1; id <= 10000; id++)
            catalog.Find(id);
        for (int sample = 0; sample < elapsed.Length; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            var watch = Stopwatch.StartNew();
            for (int call = 0; call < 20000; call++)
            {
                var entry = catalog.Find((uint)(call % 10001));
                checksum += entry == null ? 0 : entry.AppId;
            }

            watch.Stop();
            elapsed[sample] = watch.Elapsed.TotalMilliseconds;
            allocated[sample] = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        }

        Array.Sort(elapsed);
        Array.Sort(allocated);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "10,000-app catalog; 20,000 lookups; median of five samples: {0:F2} ms; {1:F2} MiB allocated; checksum={2}", elapsed[2], allocated[2] / 1048576.0, checksum));
        return 0;
    }
}
