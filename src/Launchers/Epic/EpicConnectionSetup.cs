using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Web.Script.Serialization;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    public sealed class EpicStartupChange
    {
        public string Kind { get; set; }
        public string Location { get; set; }
        public string Name { get; set; }
        public string Target { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public int ValueKind { get; set; }
        public bool KeyExisted { get; set; }
    }

    public sealed class EpicSetupRecord
    {
        public string Root { get; set; }
        public int Port { get; set; }
        public List<EpicStartupChange> Changes { get; set; }
    }

    // Saves the original values before touching startup routes. The journal also
    // survives a failed setup, helper exit, or extension uninstall.
    public sealed class EpicConnectionSetup
    {
        public static string StatePath => Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "UpdateStatus", "epic-connection.json");
        private readonly string statePath;
        private readonly bool windowsRoutes;
        private readonly Func<string, int, List<EpicStartupChange>> discover;
        private readonly Func<EpicStartupChange, string> read;
        private readonly Action<EpicStartupChange, string> write;
        public EpicConnectionSetup(string statePath = null,
            Func<string, int, List<EpicStartupChange>> discover = null,
            Func<EpicStartupChange, string> read = null, Action<EpicStartupChange, string> write = null)
        {
            this.statePath = statePath ?? StatePath;
            windowsRoutes = discover == null && read == null && write == null;
            this.discover = discover ?? EpicStartupRoutes.Discover;
            this.read = read ?? EpicStartupRoutes.Read;
            this.write = write ?? EpicStartupRoutes.Write;
        }
        public bool HasBackup => File.Exists(statePath);
        public EpicSetupRecord Load()
        {
            if (!HasBackup) return null;
            var info = new FileInfo(statePath);
            if (info.Length > 1024 * 1024) throw new IOException("The Epic setup backup is invalid.");
            EpicSetupRecord record;
            try { record = new JavaScriptSerializer().Deserialize<EpicSetupRecord>(File.ReadAllText(statePath)); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { throw new IOException("The Epic setup backup is invalid. It has been preserved.", error); }
            if (record == null || string.IsNullOrWhiteSpace(record.Root) || record.Port < 1024 || record.Port > 65535 ||
                record.Changes == null || record.Changes.Count == 0 || record.Changes.Count > 128 ||
                record.Changes.Any(change => change == null || change.After == null))
                throw new IOException("The Epic setup backup is invalid. Restore it before setting up again.");
            if (windowsRoutes) EpicStartupRoutes.ValidateRecord(record);
            return record;
        }
        public bool Configured(string root)
        {
            var record = Load();
            return record != null && !string.IsNullOrWhiteSpace(root) && string.Equals(Path.GetFullPath(root), record.Root, StringComparison.OrdinalIgnoreCase) &&
                record.Changes.All(change => string.Equals(read(change), change.After, StringComparison.Ordinal));
        }
        public string GetRestartArguments(string root)
        {
            var record = Load();
            if (record == null) return "";
            if (string.IsNullOrWhiteSpace(root) ||
                !string.Equals(Path.GetFullPath(root), record.Root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The saved Epic connection belongs to another installation. Remove it before restarting.");
            foreach (var change in record.Changes)
            {
                string current = read(change);
                if (string.Equals(current, change.After, StringComparison.Ordinal)) continue;
                // Epic recreates this exact clean command when it opens. Restart
                // can use the saved opt-in arguments without rewriting that route.
                if (change.Kind == "protocol" && change.Target != null &&
                    string.Equals(current, change.Target, StringComparison.Ordinal)) continue;
                throw new IOException("Epic startup settings changed. Remove or repair the connection before restarting.");
            }
            return Arguments(record.Port);
        }
        public void Enable(string root)
        {
            root = Path.GetFullPath(root);
            if (HasBackup)
            {
                var previous = Load();
                if (!string.Equals(previous.Root, root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Remove the previous Epic connection before selecting another installation.");
                var currentValues = previous.Changes.Select(change => read(change)).ToArray();
                for (int index = 0; index < previous.Changes.Count; index++)
                {
                    var change = previous.Changes[index];
                    var current = currentValues[index];
                    if (!string.Equals(current, change.After, StringComparison.Ordinal) &&
                        !IsOriginalValue(change, current))
                        throw new IOException("Epic startup settings changed. Newer values were preserved.");
                }
                foreach (var change in previous.Changes)
                {
                    var current = read(change);
                    if (string.Equals(current, change.After, StringComparison.Ordinal)) continue;
                    if (!IsOriginalValue(change, current))
                        throw new IOException("Epic startup settings changed. Newer values were preserved.");
                    write(change, change.After);
                }
                if (!Configured(root)) throw new IOException("Epic startup settings could not be verified.");
                return;
            }
            int port;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
            var changes = discover(root, port);
            if (changes.Count > 128) throw new IOException("Too many Epic startup routes. No settings were changed.");
            if (changes.Count == 0) throw new IOException("No supported Epic startup routes were found.");
            var record = new EpicSetupRecord { Root = root, Port = port, Changes = changes };
            string json = new JavaScriptSerializer().Serialize(record);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
                throw new IOException("The Epic setup backup would be too large. No startup settings were changed.");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream)) writer.Write(json);
                // Publish a complete backup before any startup mutation. Move
                // never replaces an existing setup record from another caller.
                File.Move(temporary, statePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            try
            {
                foreach (var change in changes)
                {
                    if (!string.Equals(read(change), change.Before, StringComparison.Ordinal))
                        throw new IOException("Epic startup settings changed during setup. No newer values were overwritten.");
                    write(change, change.After);
                }
                if (!Configured(root)) throw new IOException("Epic startup settings could not be verified.");
            }
            catch
            {
                // Removal skips untouched routes and preserves user edits.
                // Keep the journal if a permissions error prevents rollback.
                try { Remove(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        private static bool IsOriginalValue(EpicStartupChange change, string current)
        {
            if (string.Equals(current, change.Before, StringComparison.Ordinal)) return true;
            // Epic can recreate its clean per-user protocol command on startup,
            // even if only a machine-level command existed before our setup.
            // Preserve that exact original command instead of treating it as an edit.
            return change.Kind == "protocol" && change.Before == null && change.Target != null &&
                string.Equals(current, change.Target, StringComparison.Ordinal);
        }
        public void Remove()
        {
            var record = Load();
            if (record == null) return;
            bool conflict = false;
            foreach (var change in record.Changes)
            {
                string current;
                try { current = read(change); }
                catch (IOException) { conflict = true; continue; }
                if (IsOriginalValue(change, current)) continue;
                if (!string.Equals(current, change.After, StringComparison.Ordinal)) { conflict = true; continue; }
                write(change, change.Before);
                if (!string.Equals(read(change), change.Before, StringComparison.Ordinal)) conflict = true;
            }
            if (conflict) throw new IOException("Some Epic startup settings were changed elsewhere. They were preserved; the backup remains for manual recovery.");
            File.Delete(statePath);
        }
        public static string Arguments(int port)
        {
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            return "-cefdebug=" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " --remote-debugging-address=127.0.0.1";
        }
    }
}
