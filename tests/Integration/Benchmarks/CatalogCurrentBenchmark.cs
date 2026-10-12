using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PlayniteUpdateStatus.Launchers.Steam;

internal static class CatalogCurrentBenchmark
{
    public static int Run()
    {
        AppDomain.MonitoringIsEnabled = true;
        const int appCount = 1024, reads = 1000;
        var apps = Enumerable.Range(1, appCount).Select(id => new SteamCatalogApp((uint)id, "Synthetic app")).ToList().AsReadOnly();
        string rows = string.Join(",", apps.Select(app => app.AppId.ToString(CultureInfo.InvariantCulture) + ":4:-1"));
        var sample = SteamQueueSample.Parse("Q3|1|0|0|1|" + rows + "|-", apps.Select(app => app.AppId));
        sample.ReceivedUtc = DateTime.UtcNow.AddHours(-1);
        using (var catalog = new SteamUpdateCatalog(() => null, () => { throw new Exception("Benchmark must not discover Steam"); },
            inventory => { throw new Exception("Benchmark must not open a Steam reader"); }, (message, warning) => { }))
        {
            typeof(SteamUpdateCatalog).GetField("apps", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(catalog, apps);
            typeof(SteamUpdateCatalog).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(catalog, new SteamCatalogSnapshot(apps, sample, null));
            for (int warm = 0; warm < 20; warm++) { var ignored = catalog.Current; }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            int changes = 0;
            SteamCatalogSnapshot previous = null;
            var watch = Stopwatch.StartNew();
            for (int read = 0; read < reads; read++)
            {
                var snapshot = catalog.Current;
                if (snapshot.Live || snapshot.Entries.Count != appCount) throw new Exception("Expired snapshot leaked live state or lost inventory");
                if (!ReferenceEquals(previous, snapshot)) changes++;
                previous = snapshot;
            }
            watch.Stop();
            long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
            Console.WriteLine("{\"apps\":" + appCount + ",\"reads\":" + reads + ",\"milliseconds\":" +
                watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + ",\"allocatedBytes\":" + allocated +
                ",\"snapshotChanges\":" + changes + "}");
        }
        return 0;
    }
}
