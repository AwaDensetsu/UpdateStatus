using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Private Steam DLL code only runs in our x64 helper, never in the Playnite process.
    public sealed class SteamLiveReader : IDisposable
    {
        private readonly object gate = new object();
        private readonly uint appId;
        private readonly Action<string, bool> log;
        private readonly string root;
        private Process process;
        private SteamLiveSample latest;
        private DateTime receivedAt, attemptedAt = DateTime.MinValue;
        private bool disposed, permanentlyUnavailable;
        private string lastError;
        private string availabilityNotice;
        public string AvailabilityNotice { get { lock (gate) return availabilityNotice; } }
        public SteamLiveReader(string root, uint appId, Action<string, bool> log)
        { this.root = root; this.appId = appId; this.log = log; }
        public SteamLiveSample TryRead()
        {
            lock (gate)
            {
                if (disposed) return null;
                if (process != null && (DateTime.UtcNow - (latest == null ? attemptedAt : receivedAt)).TotalSeconds > 12)
                {
                    Error("Live Steam reader timed out; restarting only the helper");
                    StopChild(); latest = null;
                }
                if (process == null && !permanentlyUnavailable && (DateTime.UtcNow - attemptedAt).TotalSeconds >= 5) Start();
                return latest != null && (DateTime.UtcNow - receivedAt).TotalSeconds <= 5 ? latest : null;
            }
        }
        private void Start()
        {
            attemptedAt = DateTime.UtcNow;
            string helper = SteamHelperProcess.ExecutablePath;
            if (!File.Exists(helper)) { permanentlyUnavailable = true; availabilityNotice = "Live progress unavailable — reinstall this extension"; Error("Live helper missing; using manifest/unknown fallback"); return; }
            try
            {
                var child = SteamHelperProcess.Create(helper,
                    SteamLocator.QuoteRoot(root) + " " + appId.ToString(CultureInfo.InvariantCulture) + " 86400 --protocol " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                child.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null || !e.Data.StartsWith("S3|", StringComparison.Ordinal)) return;
                    try
                    {
                        var sample = SteamLiveSample.Parse(e.Data, appId);
                        lock (gate) { if (disposed || process != child) return; sample.ReceivedAtUtc = receivedAt = DateTime.UtcNow; latest = sample; availabilityNotice = null; }
                    }
                    catch (FormatException) { lock (gate) { if (!disposed && process == child) { latest = null; Error("Invalid live reader frame; numeric progress suppressed"); } } }
                };
                child.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (gate)
                    {
                        if (disposed || process != child) return;
                        if (e.Data.Contains("Unverified Steam client ABI") || e.Data.Contains("Unexpected ") || e.Data.Contains("NotSupportedException")) { permanentlyUnavailable = true; availabilityNotice = "Live progress unavailable for this client version — check for a plugin update"; }
                        Error("Steam reader: " + e.Data.Substring(0, Math.Min(e.Data.Length, 500)));
                    }
                };
                child.Exited += (s, e) => { lock (gate) { if (process == child) { process = null; latest = null; } child.Dispose(); } };
                process = child;
                child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
                child.EnableRaisingEvents = true;
                if (child.HasExited) { process = null; child.Dispose(); }
                log("Started isolated live Steam reader for AppID " + appId, false);
            }
            catch (Exception e) { if (process != null) { process.Dispose(); process = null; } Error("Live reader could not start: " + e.Message); }
        }
        private void Error(string message) { if (lastError != message) { lastError = message; log(message, false); } }
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true; latest = null;
                StopChild();
            }
        }
        private void StopChild()
        {
            if (process == null) return;
            var child = process; process = null;
            SteamHelperProcess.Stop(child);
        }
    }
}
