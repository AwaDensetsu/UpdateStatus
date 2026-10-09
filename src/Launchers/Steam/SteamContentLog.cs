using System;
using System.IO;
using System.Text;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Tail only new lines for this launch/AppID. Never use historical log state as current state.
    public sealed class SteamContentLog
    {
        private readonly string path, marker;
        private long offset;
        private string pending = "";
        private DateTime creation;
        public bool Active { get; private set; }
        public bool Started { get; private set; }
        public bool Paused { get; private set; }
        public string Error { get; private set; }
        public SteamContentLog(string path, uint appId)
        {
            this.path = path; marker = "AppID " + appId + " ";
            if (File.Exists(path)) { offset = new FileInfo(path).Length; creation = File.GetCreationTimeUtc(path); }
        }
        public void Poll()
        {
            if (!File.Exists(path)) return;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var currentCreation = File.GetCreationTimeUtc(path);
                if (stream.Length < offset || currentCreation != creation)
                { offset = 0; pending = ""; Active = false; Error = null; creation = currentCreation; }
                // Bound work on unexpectedly large append; missing state stays unknown.
                if (stream.Length - offset > 256 * 1024) { offset = stream.Length - 256 * 1024; pending = ""; }
                stream.Position = offset;
                byte[] bytes = new byte[Math.Min(256 * 1024, stream.Length - offset)];
                int count = stream.Read(bytes, 0, bytes.Length); offset += count;
                pending += Encoding.UTF8.GetString(bytes, 0, count);
                int end;
                while ((end = pending.IndexOf('\n')) >= 0)
                { string line = pending.Substring(0, end).TrimEnd('\r'); pending = pending.Substring(end + 1); Apply(line); }
                if (pending.Length > 8192) pending = "";
            }
        }
        public void Apply(string line)
        {
            if (!line.Contains(marker)) return;
            if (line.Contains("update started :") || line.Contains("App update changed : Running Update"))
            { Active = true; Started = true; Paused = false; Error = null; }
            if (line.Contains("App update changed : None") || line.Contains("state changed : Fully Installed," ) && !line.Contains("Update"))
                Active = false;
            bool suspended = line.Contains("Suspended") || line.Contains("Paused");
            if (suspended && (line.Contains("update canceled") || line.Contains("scheduler finished") || line.Contains("state changed")))
            { Active = false; Paused = true; Error = null; return; }
            if (line.Contains("update canceled") || line.Contains("update cancelled") || line.Contains("update failed") ||
                line.Contains("scheduler finished") && !line.Contains("result No Error"))
            { Active = false; Error = "Update interrupted"; }
            // Ownership ticket failures are unrelated to download success; deliberately ignore.
        }
    }
}
