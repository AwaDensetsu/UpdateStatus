using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Display history only. Never passed to launch/queue/completion decisions.
    public sealed class SteamCoverHistory
    {
        private const string Header = "SteamUpdateBadges|1";
        private readonly object gate = new object();
        private readonly HashSet<uint> pending = new HashSet<uint>();
        private readonly string path;
        private readonly Action<string,bool> log;
        private bool dirty;
        private DateTime retryAfter;
        private DateTime lastReceived, clearedUtc;
        public SteamCoverHistory(string path, Action<string,bool> log)
        {
            this.path = path; this.log = log;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length > 65536) throw new FormatException("History is too large");
                var rows = File.ReadAllLines(path);
                if (rows.Length == 0 || rows[0] != Header || rows.Length > 4097) throw new FormatException("Unsupported history");
                var loaded = new HashSet<uint>();
                foreach (string row in rows.Skip(1))
                {
                    uint appId;
                    if (!uint.TryParse(row,NumberStyles.None,CultureInfo.InvariantCulture,out appId) || appId == 0 || !loaded.Add(appId))
                        throw new FormatException("Invalid history row");
                }
                pending.UnionWith(loaded);
            }
            catch (IOException) { log("Cover update history could not be read",false); }
            catch (UnauthorizedAccessException) { log("Cover update history access unavailable",false); }
            catch (FormatException) { log("Invalid cover update history ignored; waiting for live Steam state",false); }
        }
        public IList<uint> PendingAppIds { get { lock (gate) return pending.OrderBy(id => id).ToList().AsReadOnly(); } }
        // Display snapshots need membership only. Copy under the existing lock
        // without sorting or exposing the mutable set to the UI thread.
        internal void CopyPendingTo(HashSet<uint> target) { lock (gate) target.UnionWith(pending); }
        public void Clear()
        {
            lock (gate)
            {
                pending.Clear();
                dirty = false;
                retryAfter = DateTime.MinValue;
                // Ignore a live callback captured before removal.
                clearedUtc = DateTime.UtcNow;
                lastReceived = DateTime.MinValue;
                if (string.IsNullOrEmpty(path)) return;
                try { File.Delete(path); }
                catch (IOException) { log("Removed Steam history could not be deleted", true); }
                catch (UnauthorizedAccessException) { log("Removed Steam history could not be deleted", true); }
            }
        }
        public void Observe(SteamCatalogSnapshot catalog)
        {
            if (catalog == null || !catalog.Fresh) return;
            lock (gate)
            {
                if (catalog.ReceivedUtc < lastReceived || catalog.ReceivedUtc <= clearedUtc) return;
                lastReceived = catalog.ReceivedUtc;
                foreach (var entry in catalog.Entries)
                {
                    if (entry.RequiresUpdate == true) dirty |= pending.Add(entry.AppId);
                    // Missing/unknown/transient work is not proof of completion.
                    else if (entry.RequiresUpdate == false && (entry.Status == SteamCatalogStatus.UpToDate || entry.Status == SteamCatalogStatus.UpdateAvailable))
                        dirty |= pending.Remove(entry.AppId);
                }
                if (!dirty || string.IsNullOrEmpty(path) || DateTime.UtcNow < retryAfter) return;
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (pending.Count > 4096) throw new IOException("Cover update history limit exceeded");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                    File.WriteAllLines(temporary,new[] { Header }.Concat(pending.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture))),new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
                    dirty = false;
                }
                catch (IOException) { retryAfter = DateTime.UtcNow.AddSeconds(5); log("Cover update history could not be saved; keeping it in memory",false); }
                catch (UnauthorizedAccessException) { retryAfter = DateTime.UtcNow.AddSeconds(5); log("Cover update history save access unavailable; keeping it in memory",false); }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
