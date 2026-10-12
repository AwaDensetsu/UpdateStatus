using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    internal static class EpicStartupRoutes
    {
        private const string ProtocolKey = @"Software\Classes\com.epicgames.launcher\shell\open\command";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static IEnumerable<string> ShortcutFolders => new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup) }.Where(path => !string.IsNullOrEmpty(path)).Distinct();

        public static List<EpicStartupChange> Discover(string root, int port)
        {
            var changes = new List<EpicStartupChange>();
            foreach (var folder in ShortcutFolders)
            foreach (var file in EnumerateShortcuts(folder))
            {
                using (var link = new Shortcut(file))
                {
                    if (!IsEpicExecutable(link.Target, root)) continue;
                    var before = link.Arguments;
                    changes.Add(new EpicStartupChange { Kind = "shortcut", Location = file, Target = link.Target,
                        Before = before, After = Append(before, port) });
                }
            }
            using (var merged = Registry.ClassesRoot.OpenSubKey(@"com.epicgames.launcher\shell\open\command"))
            using (var user = Registry.CurrentUser.OpenSubKey(ProtocolKey))
            {
                var command = merged?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var before = user?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (command != null && SplitCommand(command, root, out var prefix, out var tail))
                    changes.Add(new EpicStartupChange { Kind = "protocol", Location = ProtocolKey, Name = "",
                        Target = command, Before = before,
                        After = prefix + " " + Append(tail.TrimStart(), port),
                        ValueKind = (int)(before == null ? RegistryValueKind.String : user.GetValueKind("")),
                        KeyExisted = user != null });
            }
            using (var startup = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (startup != null)
                foreach (var name in startup.GetValueNames())
                {
                    var command = startup.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (command == null || !SplitCommand(command, root, out var prefix, out var tail)) continue;
                    changes.Add(new EpicStartupChange { Kind = "startup", Location = RunKey, Name = name,
                        Target = command, Before = command, After = prefix + " " + Append(tail.TrimStart(), port),
                        ValueKind = (int)startup.GetValueKind(name), KeyExisted = true });
                }
            }
            return changes;
        }
        private static IEnumerable<string> EnumerateShortcuts(string folder)
        {
            if (!Directory.Exists(folder)) yield break;
            // Avoid traversing junctions or someone else's profile.
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk")) yield return file;
            foreach (var directory in Directory.EnumerateDirectories(folder))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    foreach (var file in EnumerateShortcuts(directory)) yield return file;
        }
        private static bool IsEpicExecutable(string file, string root)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(file));
            return new[] { "Win32", "Win64" }.Any(platform => string.Equals(full,
                Path.Combine(root, "Portal", "Binaries", platform, "EpicGamesLauncher.exe"), StringComparison.OrdinalIgnoreCase));
        }
        private static bool SplitCommand(string command, string root, out string prefix, out string tail)
        {
            var match = Regex.Match(command, "^\\s*\"([^\"]+)\"(.*)$");
            prefix = tail = null;
            if (!match.Success || !IsEpicExecutable(match.Groups[1].Value, root)) return false;
            prefix = command.Substring(0, command.Length - match.Groups[2].Value.Length);
            tail = match.Groups[2].Value;
            return true;
        }
        private static string Append(string before, int port)
        {
            if (Regex.IsMatch(before ?? "", @"(?:^|\s)(?:-cefdebug|--remote-debugging-(?:port|address))(?:=|\s|$)", RegexOptions.IgnoreCase))
                throw new IOException("Epic already has debugging arguments. Preserve or remove that setup before connecting here.");
            return string.IsNullOrWhiteSpace(before) ? EpicConnectionSetup.Arguments(port) :
                before + " " + EpicConnectionSetup.Arguments(port);
        }
        private static void Validate(EpicStartupChange change)
        {
            if (change.Kind == "shortcut")
            {
                var full = Path.GetFullPath(change.Location);
                if (Path.GetExtension(full).Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    ShortcutFolders.Any(folder => full.StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                    return;
            }
            else if (change.Kind == "protocol" && change.Location == ProtocolKey && change.Name == "") return;
            else if (change.Kind == "startup" && change.Location == RunKey && !string.IsNullOrWhiteSpace(change.Name)) return;
            throw new IOException("The Epic startup backup contains an unsupported location.");
        }
        public static void ValidateRecord(EpicSetupRecord record)
        {
            foreach (var change in record.Changes)
            {
                Validate(change);
                string expected;
                if (change.Kind == "shortcut")
                {
                    if (!IsEpicExecutable(change.Target, record.Root) || change.Before == null)
                        throw new IOException("The backup does not describe an Epic shortcut.");
                    expected = Append(change.Before, record.Port);
                }
                else
                {
                    if (!SplitCommand(change.Target ?? "", record.Root, out var prefix, out var tail) ||
                        (change.Before != null && change.Before != change.Target) ||
                        (change.ValueKind != (int)RegistryValueKind.String && change.ValueKind != (int)RegistryValueKind.ExpandString))
                        throw new IOException("The backup does not describe an Epic startup command.");
                    expected = prefix + " " + Append(tail.TrimStart(), record.Port);
                }
                if (change.After != expected)
                    throw new IOException("The Epic startup backup has unsupported changes. It has been preserved.");
            }
        }
        public static string Read(EpicStartupChange change)
        {
            Validate(change);
            if (change.Kind == "shortcut")
            {
                if (!File.Exists(change.Location)) throw new IOException("An Epic shortcut was removed. The startup backup has been preserved.");
                using (var link = new Shortcut(change.Location))
                {
                    if (!string.Equals(link.Target, change.Target, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("An Epic shortcut now points elsewhere. It has not been changed.");
                    return link.Arguments;
                }
            }
            using (var key = Registry.CurrentUser.OpenSubKey(change.Location))
                return key?.GetValue(change.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        public static void Write(EpicStartupChange change, string value)
        {
            Validate(change);
            if (change.Kind == "shortcut")
            {
                using (var link = new Shortcut(change.Location))
                {
                    if (!string.Equals(link.Target, change.Target, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("An Epic shortcut now points elsewhere. It has not been changed.");
                    link.Arguments = value ?? "";
                    link.Save();
                }
                return;
            }
            if (value != null)
            {
                using (var key = Registry.CurrentUser.CreateSubKey(change.Location))
                    key.SetValue(change.Name, value, (RegistryValueKind)change.ValueKind);
            }
            else
            {
                using (var key = Registry.CurrentUser.OpenSubKey(change.Location, true))
                {
                    key?.DeleteValue(change.Name, false);
                    if (key == null || change.KeyExisted || key.ValueCount != 0 || key.SubKeyCount != 0) return;
                }
                Registry.CurrentUser.DeleteSubKey(change.Location, false);
            }
        }
        // Use the Unicode Windows shell interface. WScript.Shell can round-trip
        // shortcut arguments through the legacy ANSI interface and lose characters.
        private sealed class Shortcut : IDisposable
        {
            private readonly object instance;
            private readonly IShellLinkW link;
            private readonly string path;
            public Shortcut(string path)
            {
                this.path = path;
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
                try
                {
                    link = (IShellLinkW)instance;
                    ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Load(path, 0);
                }
                catch { Marshal.FinalReleaseComObject(instance); throw; }
            }
            public string Target
            {
                get { var value = new StringBuilder(32768); link.GetPath(value, value.Capacity, IntPtr.Zero, 4); return value.ToString(); }
            }
            public string Arguments
            {
                get { var value = new StringBuilder(32768); link.GetArguments(value, value.Capacity); return value.ToString(); }
                set { link.SetArguments(value); }
            }
            public void Save() => ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Save(path, true);
            public void Dispose() { Marshal.FinalReleaseComObject(instance); }
        }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
            void GetIDList(out IntPtr item);
            void SetIDList(IntPtr item);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int command);
            void SetShowCmd(int command);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }
    }
}
