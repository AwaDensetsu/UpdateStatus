using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

// Read-only production-reader probe. Never opens a game or calls download controls.
internal static class ReaderResourceBenchmark
{
    public static int Run(uint appId, bool catalogOnly)
    {
        string root = new SteamLocator().FindRoot();
        if (root == null) throw new InvalidOperationException("Steam installation unavailable");
        var identity = new LaunchIdentity { GameId = Guid.NewGuid(), LauncherId = appId.ToString(CultureInfo.InvariantCulture), Name = "Read-only reader probe" };
        var catalog = new SteamUpdateCatalog((s,w) => { });
        SteamLiveReader live = null;
        IUpdateSession observer = null;
        try
        {
            catalog.Start();
            bool shared = false;
            if (!catalogOnly)
            {
                live = new SteamLiveReader(root,appId,(s,w) => { });
                var adapter = new SteamUpdateAdapter((s,w) => { });
                var factory = typeof(SteamUpdateAdapter).GetMethod("OpenSelected",BindingFlags.Instance | BindingFlags.NonPublic);
                shared = factory != null;
                observer = shared ? (IUpdateSession)factory.Invoke(adapter,new object[] { identity,root,new Func<SteamLiveSample>(live.TryRead) }) : adapter.Open(identity);
            }
            for (int pass = 0; pass < 80; pass++)
            {
                if (live != null) live.TryRead();
                if (observer != null) observer.Poll();
                if (catalog.Current.Fresh && (catalogOnly || live.TryRead() != null && live.TryRead().HasCounters)) break;
                Thread.Sleep(100);
            }
            if (!catalog.Current.Fresh || !catalogOnly && (live.TryRead() == null || !live.TryRead().HasCounters))
                throw new InvalidOperationException("Fresh reader data unavailable for measurement");
            Console.WriteLine("READY parent="+Process.GetCurrentProcess().Id+"; catalogOnly="+catalogOnly+"; shared="+shared);
            ulong first = live == null ? 0 : live.TryRead().Downloaded, last = first;
            int valid = 0;
            for (int pass = 0; pass < 12; pass++)
            {
                var sample = live == null ? null : live.TryRead();
                if (sample != null && sample.HasCounters) { valid++; last = sample.Downloaded; }
                if (observer != null) observer.Poll();
                Thread.Sleep(1000);
            }
            Console.WriteLine("RESULT valid="+valid+"; downloadedFirst="+first+"; downloadedLast="+last+"; catalogFresh="+catalog.Current.Fresh);
            return 0;
        }
        finally
        {
            if (observer != null) observer.Dispose();
            if (live != null) live.Dispose();
            catalog.Dispose();
        }
    }
}
