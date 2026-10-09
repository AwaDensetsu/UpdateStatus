using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Reads only lines appended after this Play. Old launch history is never evidence.
    public sealed class SteamLaunchJournal
    {
        private readonly string path;
        private readonly uint appId;
        private long offset;
        private DateTime creation;
        private string partial = "", actionId;
        private bool invalid, waited, progressed;
        public string OriginalUri { get; private set; }
        public string Task { get; private set; }
        public bool Superseded { get; private set; }
        public bool WaitingForUpdate { get { return !invalid && !progressed && !Superseded && OriginalUri != null && actionId != null && Task == "DownloadingDepots"; } }

        public SteamLaunchJournal(string path, uint appId)
        {
            this.path = path; this.appId = appId;
            try { if (File.Exists(path)) { offset = new FileInfo(path).Length; creation = File.GetCreationTimeUtc(path); } }
            catch (IOException) { invalid = true; }
            catch (UnauthorizedAccessException) { invalid = true; }
        }
        public bool Poll()
        {
            if (invalid) return false;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var stamp = File.GetCreationTimeUtc(path);
                    if (creation == DateTime.MinValue) creation = stamp;
                    // A truncated/rotated/oversized journal loses the cancellation boundary.
                    if (stamp != creation || stream.Length < offset || stream.Length - offset > 256 * 1024) { invalid = true; return false; }
                    stream.Position = offset;
                    byte[] bytes = new byte[(int)(stream.Length - offset)];
                    int count = stream.Read(bytes,0,bytes.Length); offset += count;
                    partial += Encoding.UTF8.GetString(bytes,0,count);
                    int end;
                    while ((end = partial.IndexOf('\n')) >= 0)
                    { Apply(partial.Substring(0,end).TrimEnd('\r')); partial = partial.Substring(end + 1); }
                    if (partial.Length > 8192) { invalid = true; return false; }
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public void Apply(string line)
        {
            var uri = Regex.Match(line,@"ExecuteSteamURL: ""(steam://(?:rungameid/([0-9]+)|launch/([0-9]+)/Dialog))""$");
            if (uri.Success)
            {
                uint id;
                if (!uint.TryParse(uri.Groups[2].Success ? uri.Groups[2].Value : uri.Groups[3].Value,out id)) return;
                if (id != appId || OriginalUri != null) { Superseded = true; return; }
                OriginalUri = uri.Groups[1].Value;
            }
            var task = Regex.Match(line,@"GameAction \[AppID ([0-9]+), ActionID ([0-9]+)\] : LaunchApp changed task to ([A-Za-z]+) with ");
            if (!task.Success || OriginalUri == null) return;
            if (task.Groups[1].Value != appId.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Superseded = true; return; }
            if (actionId != null && actionId != task.Groups[2].Value) { Superseded = true; return; }
            actionId = task.Groups[2].Value;
            Task = task.Groups[3].Value;
            if (waited && Task != "DownloadingDepots") progressed = true;
            if (Task == "DownloadingDepots") waited = true;
        }
    }

    // Restores only the original native Steam-library request after explicit Resume.
    // Never starts an update, repeats Playnite scripts/controllers, or retries a failure.
    public sealed class SteamLaunchRecovery
    {
        private readonly object gate = new object();
        private readonly SteamLaunchJournal journal;
        private readonly Func<bool> running;
        private readonly Action<string> launch;
        private readonly Action<string,bool> log;
        private bool armed, cancelled, attempted;
        private DateTime readyAt, observedAt;
        private UpdateState current;
        public SteamLaunchRecovery(SteamLaunchJournal journal, Func<bool> running, Action<string> launch, Action<string,bool> log)
        { this.journal = journal; this.running = running; this.launch = launch; this.log = log; }
        public static SteamLaunchRecovery Create(uint appId, Func<bool> isPlayniteRunning, Action<string,bool> log)
        {
            string root = new SteamLocator().FindRoot();
            if (root == null) return null;
            return new SteamLaunchRecovery(new SteamLaunchJournal(Path.Combine(root,"logs","console_log.txt"),appId),
                () => isPlayniteRunning() || SteamLocator.IsAppRunning(appId) || !SteamLocator.IsSteamRunning(),
                uri => Process.Start(new ProcessStartInfo(Path.Combine(root,"steam.exe"),"-silent \"" + uri + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root }).Dispose(),log);
        }
        public void CommandConfirmed(UpdateCommand command)
        { lock (gate) { if (!cancelled && !attempted) armed = command == UpdateCommand.Resume; } }
        public void CommandRequested(UpdateCommand command)
        { lock (gate) { if (command == UpdateCommand.Pause) armed = false; } }
        public void Observe(UpdateState state, DateTime now)
        {
            lock (gate)
            {
                current = state; observedAt = now;
                if (!state.Visible || state.Dismissed) { cancelled = true; armed = false; }
                if (state.Phase == UpdatePhase.Ready && readyAt == DateTime.MinValue) readyAt = now;
                if (state.Phase == UpdatePhase.Updating || state.Phase == UpdatePhase.Error) readyAt = DateTime.MinValue;
            }
        }
        public void Cancel() { lock (gate) { cancelled = true; armed = false; } }
        public void Poll(DateTime now)
        {
            lock (gate)
            {
                if (cancelled || attempted) return;
                bool readable = journal.Poll();
                if (!armed || !readable || !journal.WaitingForUpdate || current == null || current.CommandPending ||
                    current.Dismissed || current.Phase != UpdatePhase.Ready && current.Phase != UpdatePhase.Launching ||
                    readyAt == DateTime.MinValue || (now - observedAt).TotalSeconds > 5) return;
                if (running()) { cancelled = true; return; }
                attempted = true; armed = false;
                try { launch(journal.OriginalUri); log("Restored original Steam Play after Resume; native launch was still waiting for a completed update",false); }
                catch (Exception e) { log("Steam launch recovery failed; no automatic retry: " + e.Message,false); }
            }
        }
    }
}
