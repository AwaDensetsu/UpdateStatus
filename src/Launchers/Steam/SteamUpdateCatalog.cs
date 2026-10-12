using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public enum SteamCatalogStatus
    {
        Unknown, UpToDate, RequiresUpdate, UpdateAvailable, RepairRequired, Queued, Updating, Paused
    }
    public sealed class SteamCatalogApp
    {
        public uint AppId
        {
            get; private set;
        }
        public string Name
        {
            get; private set;
        }
        public SteamCatalogApp(uint id, string name)
        {
            AppId = id;
            Name = string.IsNullOrWhiteSpace(name) ? "Steam app " + id : name;
        }
    }
    public sealed class SteamCatalogEntry
    {
        public uint AppId
        {
            get; private set;
        }
        public string Name
        {
            get; private set;
        }
        public SteamCatalogStatus Status
        {
            get; private set;
        }
        public bool? RequiresUpdate
        {
            get; private set;
        }
        public bool IsRunning
        {
            get; private set;
        }
        public uint? InstallState
        {
            get; private set;
        }
        public int? QueueIndex
        {
            get; private set;
        }
        private SteamCatalogEntry()
        {
        }
        public static SteamCatalogEntry Build(SteamCatalogApp app, SteamQueueItem item, SteamQueueSample sample)
        {
            var entry = new SteamCatalogEntry { AppId = app.AppId, Name = app.Name };
            if (item == null || !item.Flags.HasValue)
                return entry;
            uint flags = item.Flags.Value;
            entry.InstallState = flags;
            var manifest = new SteamManifest { Flags = flags };
            if (flags == 0 || !manifest.KnownFlags)
                return entry;
            entry.RequiresUpdate = (flags & 2) != 0;
            entry.IsRunning = (flags & 8192) != 0;
            // Install-state UpdateQueued is 8; UpdateRunning is 256.
            // Queue membership/order comes from the Steam UI queue snapshot, not byte checkpoints.
            bool queued = sample != null && sample.QueueCount.HasValue && item.QueueIndex >= 0 && item.QueueIndex < sample.QueueCount;
            if (queued)
                entry.QueueIndex = item.QueueIndex;
            bool work = (flags & SteamManifest.ActiveMask) != 0;
            if ((flags & 512) != 0 || sample != null && sample.DownloadsEnabled == false && (entry.RequiresUpdate == true || queued || work))
                entry.Status = SteamCatalogStatus.Paused;
            else if (sample != null && sample.ActiveAppId == app.AppId || (flags & 256) != 0)
                entry.Status = SteamCatalogStatus.Updating;
            else if (queued || (flags & 8) != 0 && entry.RequiresUpdate == true)
                entry.Status = SteamCatalogStatus.Queued;
            else if ((flags & (32 | 128)) != 0)
                entry.Status = SteamCatalogStatus.RepairRequired;
            else if (entry.RequiresUpdate == true)
                entry.Status = SteamCatalogStatus.RequiresUpdate;
            else if ((flags & 16) != 0)
                entry.Status = SteamCatalogStatus.UpdateAvailable;
            else if ((flags & 4) != 0 && !work)
                entry.Status = SteamCatalogStatus.UpToDate;
            else
                entry.Status = SteamCatalogStatus.Unknown;
            return entry;
        }
    }
    public sealed class SteamCatalogSnapshot
    {
        private readonly Dictionary<uint, SteamCatalogEntry> byAppId = new Dictionary<uint, SteamCatalogEntry>();
        private readonly UpdateDisplay activeDisplay;
        public IList<SteamCatalogEntry> Entries
        {
            get; private set;
        }
        public DateTime ReceivedUtc
        {
            get; private set;
        }
        public bool Live
        {
            get; private set;
        }
        public bool Fresh
        {
            get
            {
                var age = DateTime.UtcNow - ReceivedUtc;
                return Live && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(5);
            }
        }
        public bool? DownloadsEnabled
        {
            get; private set;
        }
        public uint? ActiveAppId
        {
            get; private set;
        }
        public uint? QueueCount
        {
            get; private set;
        }
        public string Notice
        {
            get; private set;
        }
        public SteamCatalogSnapshot(IEnumerable<SteamCatalogApp> apps, SteamQueueSample sample, string notice)
        {
            var rows = sample == null ? new Dictionary<uint, SteamQueueItem>() : sample.Items.ToDictionary(item => item.AppId);
            Entries = apps.OrderBy(app => app.AppId).Select(app => { SteamQueueItem item; rows.TryGetValue(app.AppId, out item); return SteamCatalogEntry.Build(app, item, sample); }).ToList().AsReadOnly();
            // Preserve ordered public entries and the first-match behavior for
            // duplicate inventory IDs while sharing one immutable lookup index.
            foreach (var entry in Entries)
                if (!byAppId.ContainsKey(entry.AppId))
                    byAppId.Add(entry.AppId, entry);
            Live = sample != null;
            ReceivedUtc = sample == null ? DateTime.MinValue : sample.ReceivedUtc;
            DownloadsEnabled = sample == null ? null : sample.DownloadsEnabled;
            ActiveAppId = sample == null ? null : sample.ActiveAppId;
            QueueCount = sample == null ? null : sample.QueueCount;
            Notice = notice;
            if (sample != null && sample.ActiveUpdate != null && sample.ActiveUpdate.AppId == ActiveAppId)
                activeDisplay = SteamLivePresentation.Build(sample.ActiveUpdate);
        }
        public UpdateDisplay DisplayForActiveApp(uint appId)
        {
            if (!Fresh || ActiveAppId != appId || activeDisplay == null)
                return null;
            // Snapshot owns its copied measurements; UI consumers never mutate
            // the catalog or the next reader sample through this display value.
            return new UpdateDisplay
            {
                Downloaded = activeDisplay.Downloaded,
                DownloadTotal = activeDisplay.DownloadTotal,
                DownloadPercent = activeDisplay.DownloadPercent,
                WorkPercent = activeDisplay.WorkPercent,
                Stage = activeDisplay.Stage,
                Paused = activeDisplay.Paused,
                WaitingForData = activeDisplay.WaitingForData,
                SecondsRemaining = activeDisplay.SecondsRemaining
            };
        }
        public SteamCatalogEntry Find(uint appId)
        {
            SteamCatalogEntry entry;
            return byAppId.TryGetValue(appId, out entry) ? entry : null;
        }
    }
    public static class SteamCatalogDiscovery
    {
        public static IList<SteamCatalogApp> Read(string root, Action<string, bool> log)
        {
            var apps = new Dictionary<uint, SteamCatalogApp>();
            if (root == null)
                return apps.Values.ToList();
            foreach (string library in new SteamLocator().Libraries(root))
            {
                try
                {
                    string folder = Path.Combine(library, "steamapps");
                    if (!Directory.Exists(folder))
                        continue;
                    foreach (string path in Directory.GetFiles(folder, "appmanifest_*.acf"))
                    {
                        try
                        {
                            var name = Regex.Match(Path.GetFileName(path), @"^appmanifest_([0-9]+)\.acf$");
                            uint id;
                            if (!name.Success || !uint.TryParse(name.Groups[1].Value, out id) || id == 0)
                                continue;
                            var manifest = SteamManifestParser.Parse(SteamManifestParser.ReadShared(path), id);
                            if (!apps.ContainsKey(id))
                                apps.Add(id, new SteamCatalogApp(id, manifest.Name));
                        }
                        catch (IOException) { log("Catalog manifest temporarily unreadable", true); }
                        catch (UnauthorizedAccessException) { log("Catalog manifest access unavailable", true); }
                        catch (FormatException) { log("Invalid catalog manifest ignored", true); }
                    }
                }
                catch (IOException) { log("Catalog library temporarily unreadable", true); }
                catch (UnauthorizedAccessException) { log("Catalog library access unavailable", true); }
            }
            if (apps.Count > 1024)
                throw new NotSupportedException("Steam catalog exceeds the supported 1024 local apps");
            return apps.Values.OrderBy(app => app.AppId).ToList().AsReadOnly();
        }
    }
    // Background inventory only. No launch, download, queue or metadata mutation API.
    public sealed class SteamUpdateCatalog : IDisposable
    {
        private readonly object gate = new object();
        private readonly Func<string> steamSession;
        private readonly Func<IList<SteamCatalogApp>> discover;
        private readonly Func<IList<SteamCatalogApp>, ISteamQueueReader> open;
        private readonly Action<string, bool> log;
        private Timer timer;
        private ISteamQueueReader reader;
        private IList<SteamCatalogApp> apps = new List<SteamCatalogApp>().AsReadOnly();
        private SteamCatalogSnapshot current;
        private SteamCatalogSnapshot expiredSource, expiredSnapshot;
        private string session, lastSummary;
        private readonly Dictionary<uint, string> lastEntries = new Dictionary<uint, string>();
        private DateTime nextDiscovery;
        private long generation;
        private int busy;
        private bool disposed;
        public event Action<SteamCatalogSnapshot> Changed;
        public SteamCatalogSnapshot Current
        {
            get
            {
                lock (gate)
                {
                    if (!current.Live || current.Fresh)
                    {
                        expiredSource = expiredSnapshot = null;
                        return current;
                    }
                    // Share one unavailable view per source snapshot. Repeated UI
                    // reads must not rebuild the full inventory after data expires.
                    if (expiredSource != current)
                    {
                        expiredSnapshot = new SteamCatalogSnapshot(apps, null, "Steam catalog data expired");
                        expiredSource = current;
                    }
                    return expiredSnapshot;
                }
            }
        }
        public SteamUpdateCatalog(Action<string, bool> log)
        {
            string root = null;
            this.log = log;
            steamSession = SteamLocator.SessionId;
            discover = () =>
            {
                root = new SteamLocator().FindRoot();
                return SteamCatalogDiscovery.Read(root, log);
            };
            open = found => new SteamQueueReader(root, found.Select(app => app.AppId), log);
            current = new SteamCatalogSnapshot(apps, null, "Steam catalog not started");
        }
        public SteamUpdateCatalog(Func<string> steamSession, Func<IList<SteamCatalogApp>> discover, Func<IList<SteamCatalogApp>, ISteamQueueReader> open, Action<string, bool> log)
        {
            this.steamSession = steamSession;
            this.discover = discover;
            this.open = open;
            this.log = log;
            current = new SteamCatalogSnapshot(apps, null, "Steam catalog not started");
        }
        public void Start()
        {
            lock (gate)
            {
                if (disposed || timer != null)
                    return;
                long token = ++generation;
                nextDiscovery = DateTime.MinValue;
                timer = new Timer(_ => Tick(token), null, 0, 1000);
            }
        }
        public void RefreshSoon()
        {
            long token;
            lock (gate)
            {
                if (disposed || timer == null)
                    return;
                nextDiscovery = DateTime.MinValue;
                token = generation;
            }
            ThreadPool.QueueUserWorkItem(_ => Tick(token));
        }
        private void Tick(long token)
        {
            if (Interlocked.Exchange(ref busy, 1) != 0)
                return;
            try
            {
                string key = steamSession() ?? "";
                bool rescan;
                lock (gate)
                {
                    if (disposed || timer == null || token != generation)
                        return;
                    if (key != session)
                    {
                        if (reader != null)
                            reader.Dispose();
                        reader = null;
                        session = key;
                        nextDiscovery = DateTime.MinValue;
                    }
                    rescan = DateTime.UtcNow >= nextDiscovery;
                }
                IList<SteamCatalogApp> found = rescan ? discover() : null;
                SteamCatalogSnapshot next;
                lock (gate)
                {
                    if (disposed || timer == null || token != generation)
                        return;
                    if (found != null)
                    {
                        var ordered = found.OrderBy(app => app.AppId).ToList().AsReadOnly();
                        bool changed = !apps.Select(app => app.AppId).SequenceEqual(ordered.Select(app => app.AppId));
                        apps = ordered;
                        nextDiscovery = DateTime.UtcNow.AddSeconds(5);
                        if (changed && reader != null)
                        {
                            reader.Dispose();
                            reader = null;
                        }
                    }
                    if (key.Length > 0 && reader == null)
                        reader = open(apps);
                    var sample = reader == null ? null : reader.TryRead();
                    if (sample != null && (sample.ReceivedUtc > DateTime.UtcNow || (DateTime.UtcNow - sample.ReceivedUtc).TotalSeconds > 5))
                        sample = null;
                    next = current = new SteamCatalogSnapshot(apps, sample, key.Length == 0 ? "Steam is closed" : sample == null ? reader.Notice ?? "Waiting for Steam catalog data" : null);
                    string summary = "Steam catalog: live=" + next.Live + "; apps=" + apps.Count + "; requiresUpdate=" + next.Entries.Count(entry => entry.RequiresUpdate == true) + "; queue=" + next.QueueCount + "; active=" + next.ActiveAppId + "; paused=" + (next.DownloadsEnabled.HasValue ? (!next.DownloadsEnabled).ToString() : "unknown");
                    if (summary != lastSummary)
                    {
                        lastSummary = summary;
                        log(summary, false);
                    }
                    if (next.Live)
                        foreach (var entry in next.Entries)
                        {
                            string state = entry.Status + ":" + entry.RequiresUpdate + ":" + entry.QueueIndex;
                            string previous;
                            bool known = lastEntries.TryGetValue(entry.AppId, out previous);
                            if (state != previous && (entry.Status != SteamCatalogStatus.UpToDate && entry.Status != SteamCatalogStatus.Unknown || known))
                                log("Steam catalog game=" + entry.Name.Replace("\r", " ").Replace("\n", " ") + " AppID=" + entry.AppId + " status=" + entry.Status + " requiresUpdate=" + entry.RequiresUpdate + " queueIndex=" + entry.QueueIndex, false);
                            lastEntries[entry.AppId] = state;
                        }
                }
                NotifyChanged(next);
            }
            catch (Exception e)
            {
                SteamCatalogSnapshot next;
                lock (gate)
                {
                    if (token != generation || disposed)
                        return;
                    next = current = new SteamCatalogSnapshot(apps, null, "Steam catalog unavailable");
                }
                log("Steam catalog unavailable: " + e.GetType().Name, true);
                NotifyChanged(next);
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }
        public void Stop()
        {
            SteamCatalogSnapshot next;
            lock (gate)
            {
                ++generation;
                if (timer != null)
                {
                    timer.Dispose();
                    timer = null;
                }
                if (reader != null)
                {
                    reader.Dispose();
                    reader = null;
                }
                session = null;
                lastSummary = null;
                lastEntries.Clear();
                next = current = new SteamCatalogSnapshot(apps, null, "Steam catalog stopped");
                expiredSource = expiredSnapshot = null;
            }
            NotifyChanged(next);
        }
        private void NotifyChanged(SteamCatalogSnapshot snapshot)
        {
            // Catalog notifications are read-only; one UI observer must not invalidate
            // a verified sample or prevent other observers from receiving it.
            ObserverNotifications.Notify(Changed, subscriber => ((Action<SteamCatalogSnapshot>)subscriber)(snapshot),log,"Catalog notification");
        }
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
            }
            Stop();
        }
    }
}
