using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using PlayniteUpdateStatus.Launchers.Steam;

// Measures timer/snapshot overhead only. The injected closed Steam session
// cannot open a real helper, enumerate the user's library or change downloads.
internal static class IdleCatalogBenchmark
{
    public static int Run()
    {
        AppDomain.MonitoringIsEnabled = true;
        int discoveries = 0, opens = 0, ticks = 0;
        var apps = Enumerable.Range(1,14).Select(id => new SteamCatalogApp((uint)id,"Synthetic app")).ToList().AsReadOnly();
        using (var catalog = new SteamUpdateCatalog(() => "", () => { Interlocked.Increment(ref discoveries); return apps; },
            found => { Interlocked.Increment(ref opens); throw new Exception("Closed catalog opened a reader"); }, (message,warning) => { }))
        using (var process = Process.GetCurrentProcess())
        {
            catalog.Changed += state => Interlocked.Increment(ref ticks);
            catalog.Start();
            Thread.Sleep(1200);
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            process.Refresh(); var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            Thread.Sleep(12000);
            process.Refresh(); double cpuMs = (process.TotalProcessorTime-cpu).TotalMilliseconds;
            watch.Stop();
            double memory = (AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-before)/1048576.0;
            int atStop = ticks; catalog.Stop(); int stopped = ticks;
            Thread.Sleep(1200);
            if (opens != 0 || ticks != stopped) throw new Exception("Idle cleanup did not stop catalog work");
            Console.WriteLine("Synthetic closed catalog: CPUms="+cpuMs+"; elapsedMs="+watch.Elapsed.TotalMilliseconds+
                "; managedAllocatedMiB="+memory+"; ticksBeforeStop="+atStop+"; discoveryCalls="+discoveries+
                "; openedReaders="+opens+"; ticksAfterStop="+(ticks-stopped));
            return 0;
        }
    }
}
