using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    public sealed class EpicEndpoint
    {
        public int Port { get; }
        public uint Owner { get; }
        public EpicEndpoint(int port, uint owner) { Port = port; Owner = owner; }
    }

    // Immutable snapshots keep selection independent of WMI enumeration order.
    internal sealed class EpicProcessInfo
    {
        public uint Id { get; }
        public uint Parent { get; }
        public int Session { get; }
        public string Executable { get; }
        public string Command { get; }
        public EpicProcessInfo(uint id, uint parent, int session, string executable, string command)
        { Id = id; Parent = parent; Session = session; Executable = executable; Command = command; }
    }
    internal sealed class EpicListenerInfo
    {
        public uint Owner { get; }
        public int Port { get; }
        public string Address { get; }
        public EpicListenerInfo(uint owner, int port, string address) { Owner = owner; Port = port; Address = address; }
    }

    public static class EpicLocalAccess
    {
        public static string FindExecutable(string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                folder = folder.Trim();
                foreach (var relative in new[] { "EpicGamesLauncher.exe",
                    @"Portal\Binaries\Win64\EpicGamesLauncher.exe", @"Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe" })
                {
                    var file = Path.GetFullPath(Path.Combine(folder, relative));
                    if (File.Exists(file)) return file;
                }
                return null;
            }
            foreach (var basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                var file = Path.Combine(basePath, @"Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe");
                if (File.Exists(file)) return file;
            }
            // Respect a custom installation advertised by Windows' uninstall metadata.
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (uninstall == null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                using (var entry = uninstall.OpenSubKey(name))
                {
                    if (!string.Equals(entry?.GetValue("DisplayName") as string, "Epic Games Launcher", StringComparison.OrdinalIgnoreCase)) continue;
                    string location = entry.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        var file = FindExecutable(location);
                        if (file != null) return file;
                    }
                }
            }
            return null;
        }
        public static string FindInstallationFolder(string folder)
        {
            var executable = FindExecutable(folder);
            if (executable == null) return null;
            return InstallationFolder(executable);
        }
        private static string InstallationFolder(string executable)
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(executable));
            if (string.Equals(directory.Name, "Win64", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(directory.Parent?.Name, "Binaries", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(directory.Parent.Parent?.Name, "Portal", StringComparison.OrdinalIgnoreCase))
                return directory.Parent.Parent.Parent.FullName;
            return directory.FullName;
        }

        public static EpicEndpoint Discover(string folder = null)
        {
            string executable = FindExecutable(folder);
            if (executable == null) return null;
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            var processes = new List<EpicProcessInfo>();
            using (var query = Query("SELECT ProcessId,ParentProcessId,SessionId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='EpicGamesLauncher.exe' OR Name='EpicWebHelper.exe'"))
            using (var rows = query.Get())
            foreach (ManagementObject row in rows)
            using (row)
            {
                string path = row["ExecutablePath"] as string;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                processes.Add(new EpicProcessInfo(Convert.ToUInt32(row["ProcessId"]), Convert.ToUInt32(row["ParentProcessId"]),
                    Convert.ToInt32(row["SessionId"]), path, row["CommandLine"] as string));
            }
            // Read the socket table only after finding a valid launcher candidate.
            return SelectEndpoint(executable, session, processes, ReadListeners());
        }
        private static IEnumerable<EpicListenerInfo> ReadListeners()
        {
            using (var query = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT LocalAddress,LocalPort,OwningProcess FROM MSFT_NetTCPConnection WHERE State=2"))
            {
                query.Options.Timeout = TimeSpan.FromSeconds(2);
                using (var rows = query.Get())
                foreach (ManagementObject row in rows)
                using (row)
                    yield return new EpicListenerInfo(Convert.ToUInt32(row["OwningProcess"]),
                        Convert.ToInt32(row["LocalPort"], CultureInfo.InvariantCulture), row["LocalAddress"] as string);
            }
        }
        internal static EpicEndpoint SelectEndpoint(string executable, int session,
            IEnumerable<EpicProcessInfo> processes, IEnumerable<EpicListenerInfo> listeners)
        {
            var local = processes.Where(process => process.Session == session).ToArray();
            var mains = local.Where(process => string.Equals(Path.GetFullPath(process.Executable),
                Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (mains.Length != 1) return null;
            var main = mains[0];
            int? mainPort = DebugPort(main.Command, @"(?:^|\s)-cefdebug=(\d{1,5})(?:\s|$)");
            var candidates = new Dictionary<uint, int>();
            if (mainPort.HasValue) candidates.Add(main.Id, mainPort.Value);
            string helper = Path.Combine(InstallationFolder(executable), @"Engine\Binaries\Win64\EpicWebHelper.exe");
            foreach (var process in local)
            {
                if (process.Parent != main.Id || !string.Equals(Path.GetFullPath(process.Executable), helper, StringComparison.OrdinalIgnoreCase)) continue;
                int? port = DebugPort(process.Command, @"--remote-debugging-port[= ](\d{1,5})(?:\s|""|$)");
                if (port.HasValue && (!mainPort.HasValue || mainPort.Value == port.Value)) candidates[process.Id] = port.Value;
            }
            if (candidates.Count == 0) return null;
            var sockets = listeners.ToArray();
            var exposed = new HashSet<int>();
            foreach (var socket in sockets)
            {
                IPAddress address;
                if (!IPAddress.TryParse(socket.Address, out address) || !IPAddress.IsLoopback(address)) exposed.Add(socket.Port);
            }
            EpicEndpoint result = null;
            foreach (var socket in sockets)
            {
                int expected;
                if (socket.Address != "127.0.0.1" || exposed.Contains(socket.Port) ||
                    !candidates.TryGetValue(socket.Owner, out expected) || socket.Port != expected) continue;
                if (result != null && (result.Port != socket.Port || result.Owner != socket.Owner)) return null;
                result = new EpicEndpoint(socket.Port, socket.Owner);
            }
            return result;
        }
        private static int? DebugPort(string command, string pattern)
        {
            var match = Regex.Match(command ?? "", pattern, RegexOptions.IgnoreCase);
            int port;
            return match.Success && int.TryParse(match.Groups[1].Value, out port) && port > 0 && port <= 65535 ? (int?)port : null;
        }
        private static ManagementObjectSearcher Query(string text)
        {
            var query = new ManagementObjectSearcher(text);
            query.Options.Timeout = TimeSpan.FromSeconds(2);
            return query;
        }
    }
}
