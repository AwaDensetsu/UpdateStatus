using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamLocator
    {
        public static string ManualRoot { get; set; }
        public static string CachedRoot { get; set; }
        public static event Action<string> Located;
        public static bool IsValidRoot(string root)
        {
            try {
                if (string.IsNullOrWhiteSpace(root)) return false;
                return IsAbsolutePath(root) && File.Exists(Path.Combine(root,"steam.exe")) && File.Exists(Path.Combine(root,"steamclient64.dll"));
            }
            catch (ArgumentException) { return false; } catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; } catch (System.Security.SecurityException) { return false; }
        }
        private static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string prefix = Path.GetPathRoot(path);
            return prefix.StartsWith(@"\\",StringComparison.Ordinal) || prefix.Length == 3 && prefix[1] == ':' && (prefix[2] == '\\' || prefix[2] == '/');
        }
        public static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return "";
            string full = Path.GetFullPath(root.Trim());
            return full == Path.GetPathRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        }
        public static string QuoteRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || root.Contains("\"")) throw new ArgumentException("Invalid Steam root");
            int trailing = root.Length-root.TrimEnd('\\').Length;
            return "\"" + root + new string('\\',trailing) + "\"";
        }
        public string FindRoot()
        { return FindRoot(true); }
        public string FindAutomaticRoot()
        { return FindRoot(false); }
        private string FindRoot(bool includeManual)
        {
            var candidates = new List<string>();
            if (includeManual && !string.IsNullOrWhiteSpace(ManualRoot)) candidates.Add(ManualRoot);
            foreach (var process in Process.GetProcessesByName("steam"))
            {
                using (process)
                {
                    try { candidates.Add(Path.GetDirectoryName(process.MainModule.FileName)); }
                    catch (Exception) { /* Registry/saved location remain available. */ }
                }
            }
            AddRegistry(candidates, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath");
            AddRegistry(candidates, RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam", "InstallPath");
            AddRegistry(candidates, RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Valve\Steam", "InstallPath");
            if (!string.IsNullOrWhiteSpace(CachedRoot)) candidates.Add(CachedRoot);
            return ResolveCandidates(candidates);
        }
        // Discovery sources are ordered by freshness; the saved path is a fallback.
        public static string ResolveCandidates(IEnumerable<string> candidates)
        {
            string found = candidates.FirstOrDefault(IsValidRoot);
            if (found == null) return null;
            found = NormalizeRoot(found); CachedRoot = found;
            var handler = Located; if (handler != null) handler(found);
            return found;
        }
        private static void AddRegistry(List<string> paths, RegistryHive hive, RegistryView view, string key, string value)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var node = root.OpenSubKey(key))
                    if (node != null) paths.Add(node.GetValue(value) as string);
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
        public static IList<string> ParseLibraries(string root, string vdf)
        {
            var result = new List<string> { Path.GetFullPath(root) };
            var folders = KeyValues.Parse(vdf).Child("libraryfolders");
            if (folders == null) throw new FormatException("Missing libraryfolders");
            foreach (var pair in folders.Children)
            {
                uint index;
                if (!uint.TryParse(pair.Key, out index)) continue;
                string path = pair.Value.Value ?? pair.Value.Get("path");
                try { if (IsAbsolutePath(path)) result.Add(Path.GetFullPath(path)); }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (System.Security.SecurityException) { }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        public IList<string> Libraries(string root)
        {
            string config = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try { if (File.Exists(config)) return ParseLibraries(root, SteamManifestParser.ReadShared(config)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            // Steam can replace this metadata while we read it. Retain the known
            // primary library and retry normal discovery on the next scan.
            return new List<string> { root };
        }
        public string FindManifest(string root, uint appId, string installDirectory)
        {
            var paths = Libraries(root).Select(p => Path.Combine(p, "steamapps", "appmanifest_" + appId + ".acf")).ToList();
            // Prefer Playnite's actual install path when an app is installed in multiple libraries.
            if (!string.IsNullOrEmpty(installDirectory))
                paths = paths.OrderByDescending(p => installDirectory.StartsWith(Path.Combine(Path.GetDirectoryName(p), "common") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string path in paths) {
                try { if (File.Exists(path) && SteamManifestParser.Parse(SteamManifestParser.ReadShared(path), appId).AppId == appId) return path; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (FormatException) { }
            }
            return null;
        }
        public static bool IsAppRunning(uint appId)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps\" + appId))
                    return key != null && Convert.ToInt32(key.GetValue("Running", 0)) != 0;
            }
            catch (Exception) { return false; }
        }
        public static bool IsSteamRunning()
        {
            var processes = Process.GetProcessesByName("steam");
            foreach (var p in processes) p.Dispose();
            return processes.Length != 0;
        }
        public static string SessionId()
        {
            var processes = Process.GetProcessesByName("steam");
            try { return string.Join(",",processes.Select(process => process.Id).OrderBy(id => id)); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
