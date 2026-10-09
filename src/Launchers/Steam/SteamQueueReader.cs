using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public interface ISteamQueueReader : IDisposable
    {
        SteamQueueSample TryRead();
        string Notice { get; }
    }
    public sealed class SteamQueueReader : ISteamQueueReader
    {
        private readonly object gate = new object();
        private readonly string root;
        private readonly uint[] ids;
        private readonly Action<string,bool> log;
        private Process process;
        private SteamQueueSample latest;
        private DateTime attempted = DateTime.MinValue;
        private bool disposed, unsupported;
        private uint lastSequence;
        private string notice, lastError;
        public string Notice { get { lock (gate) return notice; } }
        public SteamQueueReader(string root, IEnumerable<uint> ids, Action<string,bool> log)
        {
            this.root = root; this.ids = ids.Distinct().OrderBy(id => id).ToArray(); this.log = log;
            if (this.ids.Length > 1024 || this.ids.Any(id => id == 0)) throw new ArgumentException("Invalid Steam catalog identities");
        }
        public SteamQueueSample TryRead()
        {
            lock (gate)
            {
                if (disposed) return null;
                var now = DateTime.UtcNow;
                if (process != null && (now - (latest == null ? attempted : latest.ReceivedUtc)).TotalSeconds > 12)
                { StopChild(); latest = null; Error("Steam catalog reader stalled; restarting only its helper"); }
                if (process == null && !unsupported && (now - attempted).TotalSeconds >= 5) Start();
                return latest != null && (now - latest.ReceivedUtc).TotalSeconds <= 5 ? latest : null;
            }
        }
        private void Start()
        {
            attempted = DateTime.UtcNow;
            string helper = SteamHelperProcess.ExecutablePath;
            if (!File.Exists(helper)) { unsupported = true; notice = "Steam catalog reader missing"; return; }
            try
            {
                string list = ids.Length == 0 ? "-" : string.Join(",",ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
                var child = SteamHelperProcess.Create(helper,SteamLocator.QuoteRoot(root) + " --catalog " + list + " 86400 " + Process.GetCurrentProcess().Id);
                child.OutputDataReceived += (sender,e) => {
                    if (e.Data == null || !(e.Data.StartsWith("Q1|",StringComparison.Ordinal) || e.Data.StartsWith("Q2|",StringComparison.Ordinal))) return;
                    try {
                        var sample = SteamQueueSample.Parse(e.Data,ids);
                        lock (gate) {
                            if (disposed || process != child) return;
                            if (sample.Sequence <= lastSequence) { latest = null; Error("Replayed Steam catalog frame refused"); return; }
                            lastSequence = sample.Sequence;
                            sample.ReceivedUtc = DateTime.UtcNow; latest = sample; notice = null;
                        }
                    } catch (FormatException) { lock (gate) { if (!disposed && process == child) { latest = null; Error("Invalid Steam catalog frame refused"); } } }
                };
                child.ErrorDataReceived += (sender,e) => {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (gate) {
                        if (disposed || process != child) return;
                        if (e.Data.Contains("NotSupportedException") || e.Data.Contains("Unverified") || e.Data.Contains("Unexpected "))
                        { unsupported = true; notice = "Steam catalog unavailable for this client version"; }
                        Error("Steam catalog: " + e.Data.Substring(0,Math.Min(500,e.Data.Length)));
                    }
                };
                child.Exited += (sender,e) => { lock (gate) { if (process == child) { process = null; latest = null; } child.Dispose(); } };
                process = child; latest = null; lastSequence = 0; child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine(); child.EnableRaisingEvents = true;
                log("Started Steam catalog reader for " + ids.Length + " installed/download-in-progress apps",false);
            }
            catch (Exception e) { StopChild(); notice = "Steam catalog connection unavailable"; Error(e.Message); }
        }
        private void Error(string message) { if (lastError != message) { lastError = message; log(message,true); } }
        private void StopChild()
        {
            if (process == null) return;
            var child = process; process = null;
            SteamHelperProcess.Stop(child);
        }
        public void Dispose() { lock (gate) { disposed = true; latest = null; StopChild(); } }
    }
}
