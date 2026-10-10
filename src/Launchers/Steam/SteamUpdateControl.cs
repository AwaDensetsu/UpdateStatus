using System;
using System.Diagnostics;
using System.IO;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamUpdateControl : IDisposable
    {
        private readonly object gate = new object();
        private readonly string root;
        private readonly uint appId;
        private readonly Action<string,bool> log;
        private Process child;
        private bool disposed;
        public bool Supported { get; private set; }
        public static bool SupportsSelection(string root)
        { return SteamUiAccess.CanConnect(root); }
        public SteamUpdateControl(string root, uint appId, Action<string,bool> log)
        {
            this.root = root; this.appId = appId; this.log = log;
            Supported = SteamUiAccess.CanConnect(root);
        }
        public bool Send(UpdateCommand command)
        {
            if (command != UpdateCommand.Pause && command != UpdateCommand.Resume) return false;
            string action = command == UpdateCommand.Pause ? "pause" : "resume";
            string reply = Execute("--ui-downloads-command " + action,"U1|");
            bool accepted = reply == "U1|" + appId + "|" + action + "|accepted" || reply == "U1|" + appId + "|" + action + "|already";
            log("Steam downloads " + action + " (all downloads; observed AppID=" + appId + "): " + (accepted ? "accepted; waiting for live confirmation" : "not accepted"),false);
            return accepted;
        }
        public SteamSelectionResult Select()
        {
            string reply = Execute("--ui-select-update","U2|");
            if (reply == "U2|" + appId + "|select|accepted" || reply == "U2|" + appId + "|select|already") return SteamSelectionResult.Accepted;
            if (reply == "U2|" + appId + "|select|finished") return SteamSelectionResult.Finished;
            if (reply == "U2|" + appId + "|select|waiting") return SteamSelectionResult.Waiting;
            return SteamSelectionResult.Rejected;
        }
        public bool SendSelected(UpdateCommand command)
        {
            if (command != UpdateCommand.Pause && command != UpdateCommand.Resume) return false;
            string action = command == UpdateCommand.Pause ? "pause" : "resume";
            string reply = Execute("--ui-selected-downloads-command " + action,"U1|");
            bool accepted = reply == "U1|" + appId + "|" + action + "|accepted" || reply == "U1|" + appId + "|" + action + "|already";
            log("Selected Steam downloads " + action + " AppID=" + appId + ": " + (accepted ? "accepted; waiting for global confirmation" : "not accepted"),false);
            return accepted;
        }
        public bool CloseUpdate()
        {
            string reply = Execute("--ui-close-update","U3|");
            bool accepted = reply == "U3|" + appId + "|close|accepted" || reply == "U3|" + appId + "|close|already";
            log("Steam close update AppID=" + appId + ": " + (accepted ? "accepted; waiting for pause and queue removal" : "not accepted"),false);
            return accepted;
        }
        private string Execute(string arguments, string protocol)
        {
            Process process;
            lock (gate)
            {
                if (disposed || !Supported || child != null) return null;
                string helper = SteamHelperProcess.ExecutablePath;
                if (root.Contains("\"") || !File.Exists(helper)) return null;
                // New switch/protocol prevents an old helper from executing queue placement.
                process = child = SteamHelperProcess.Create(helper,SteamLocator.QuoteRoot(root) + " " + appId + " " + arguments);
                try { process.Start(); }
                catch (Exception e) { child = null; process.Dispose(); log("Could not send Steam update command: " + e.Message,false); return null; }
            }
            try
            {
                // Drain both streams asynchronously; connection errors must not block.
                string reply = null;
                process.OutputDataReceived += (s,e) => { if (e.Data != null && e.Data.StartsWith(protocol,StringComparison.Ordinal)) reply = e.Data; };
                process.ErrorDataReceived += (s,e) => { if (!string.IsNullOrEmpty(e.Data)) log("Steam control: " + e.Data.Substring(0,Math.Min(500,e.Data.Length)),false); };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(6000)) { log("Steam update command timed out; no automatic retry",false); return null; }
                process.WaitForExit();
                return process.ExitCode == 0 ? reply : null;
            }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
            finally { lock (gate) { if (child == process) child = null; SteamHelperProcess.Stop(process); } }
        }
        public void Dispose() { lock (gate) { disposed = true; if (child != null) { var process = child; child = null; SteamHelperProcess.Stop(process); } } }
    }
}
