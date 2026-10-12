using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using PlayniteUpdateStatus.Launchers.Epic;
using PlayniteUpdateStatus.Launchers.Steam;

namespace PlayniteUpdateStatus.Launchers
{
    public sealed class LauncherStillRunningException : IOException
    {
        public LauncherStillRunningException(Exception cause = null) : base(
            "Restart required — the launcher is still running. Exit it completely from the system tray.", cause) { }
    }

    public static class LauncherConnectionActions
    {
        private static readonly object shutdownGate = new object();
        private static readonly Dictionary<string, Task<Win32Exception>> pendingShutdowns =
            new Dictionary<string, Task<Win32Exception>>();
        public static Task SetUpEpicAsync(string root, bool remove)
        {
            return Task.Run(() => {
                int code = RunSetup(root, remove, false);
                if (code == 5) code = RunSetup(root, remove, true);
                if (code != 0) throw new IOException(code == 3 ?
                    "Some startup settings changed elsewhere. They were preserved; check the connection backup." :
                    "Could not change the Epic connection. Check the installation folder and startup permissions.");
            });
        }
        private static int RunSetup(string root, bool remove, bool elevated)
        {
            var helper = Path.Combine(Path.GetDirectoryName(typeof(LauncherConnectionActions).Assembly.Location), "LauncherConnectionSetup.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("Reinstall Update Status; its connection setup helper is missing.");
            var start = new ProcessStartInfo(helper, (remove ? "remove" : "enable") + " " +
                SteamLocator.QuoteRoot(remove && string.IsNullOrWhiteSpace(root) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : root) + " " + WindowsIdentity.GetCurrent().User.Value)
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (elevated) start.Verb = "runas";
            using (var process = Process.Start(start))
            {
                if (process == null || !process.WaitForExit(60000))
                    throw new IOException("Connection setup is still pending. Check Windows before trying again.");
                return process.ExitCode;
            }
        }
        public static string Session(bool epic)
        {
            var processes = Process.GetProcessesByName(epic ? "EpicGamesLauncher" : "steam");
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            var ids = new List<int>();
            try
            {
                foreach (var process in processes)
                {
                    try { if (process.SessionId == session) ids.Add(process.Id); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
                return string.Join(",",ids.OrderBy(id => id));
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        public static Task RestartSteamAsync(string root) { return RestartSteamAsync(root, true); }
        private static Task RestartSteamAsync(string root, bool requestClose)
        {
            if (!SteamLocator.IsValidRoot(root)) throw new ArgumentException("Select the Steam folder first.");
            return RestartAsync(Path.Combine(root, "steam.exe"), "", requestClose);
        }
        public static Task RestartEpicAsync(string root) { return RestartEpicAsync(root, true); }
        public static Task OpenAfterExitAsync(string root, bool epic)
        { return epic ? RestartEpicAsync(root, false) : RestartSteamAsync(root, false); }
        private static Task RestartEpicAsync(string root, bool requestClose)
        {
            string executable = EpicLocalAccess.FindExecutable(root);
            if (executable == null) throw new ArgumentException("Select the Epic folder first.");
            var setup = new EpicConnectionSetup();
            string folder = EpicLocalAccess.FindInstallationFolder(root);
            return RestartAsync(executable, setup.GetRestartArguments(folder), requestClose, requestClose);
        }
        private static Task RestartAsync(string executable, string arguments, bool requestClose, bool allowTermination = false)
        {
            return Task.Run(async () => {
                string name = Path.GetFileNameWithoutExtension(executable);
                var mains = CurrentProcesses(name);
                try
                {
                    if (mains.Length > 1) throw new IOException("Multiple launcher processes are running. Exit the launcher manually first.");
                    foreach (var main in mains)
                    {
                        if (main.HasExited) continue;
                        // Hold the exact Windows process object throughout an Epic
                        // restart. Kill then uses this handle, never a reused PID.
                        if (requestClose && allowTermination)
                        {
                            _ = main.Handle;
                            if (main.HasExited) continue;
                        }
                        int session;
                        using (var current = Process.GetCurrentProcess()) session = current.SessionId;
                        string actual = ExecutablePath(main.Id, session);
                        if (actual == null && main.HasExited) continue;
                        if (actual == null) throw new IOException("Cannot verify the running launcher. Exit it manually first.");
                        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("The running launcher uses another installation. Exit it manually first.");
                    }
                    if (!requestClose && mains.Any(main => !main.HasExited)) throw new LauncherStillRunningException();
                    if (requestClose && allowTermination && mains.Length != 0)
                    {
                        var main = mains[0];
                        await CloseEpicAsync(() => main.CloseMainWindow(), () => main.Kill(),
                            () => !main.HasExited, () => Task.Delay(250), 20).ConfigureAwait(false);
                    }
                    else
                    {
                        await WaitForMainExitAsync(
                            () => mains.Length == 0 || mains[0].HasExited ? Task.FromResult<Win32Exception>(null) : RequestNormalExitAsync(mains[0]),
                            () => mains.Any(main => !main.HasExited), () => Task.Delay(250), 60).ConfigureAwait(false);
                    }
                    // Chromium helper processes can outlive their parent. They do
                    // not establish that the main launcher is still running.
                    var alreadyOpened = CurrentProcesses(name);
                    try
                    {
                        if (alreadyOpened.Length != 0)
                            throw new IOException("The launcher opened again before restart finished. Exit it completely and try again.");
                    }
                    finally { foreach (var process in alreadyOpened) process.Dispose(); }
                    using (var started = Process.Start(new ProcessStartInfo(executable, arguments) {
                        UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable),
                        WindowStyle = ProcessWindowStyle.Normal }))
                    {
                        if (started == null) throw new IOException("Could not reopen the launcher. Open it manually.");
                    }
                    for (int attempt = 0; attempt < 40; attempt++)
                    {
                        var opened = CurrentProcesses(name);
                        try
                        {
                            foreach (var process in opened)
                                if (string.Equals(ExecutablePath(process.Id), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return;
                        }
                        finally { foreach (var process in opened) process.Dispose(); }
                        await Task.Delay(250).ConfigureAwait(false);
                    }
                    throw new IOException("The launcher did not stay open after restart. Open it manually and check its status.");
                }
                finally { foreach (var main in mains) main.Dispose(); }
            });
        }
        internal static async Task CloseEpicAsync(Action requestClose, Action terminate, Func<bool> running,
            Func<Task> pause, int attempts)
        {
            if (!running()) return;
            try { requestClose(); }
            catch (InvalidOperationException) { if (running()) throw; }
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (!running()) return;
                await pause().ConfigureAwait(false);
            }
            if (!running()) return;
            // Only an explicitly confirmed Epic restart reaches this fallback.
            // Closing a tray client can hide its window while leaving it alive.
            try { terminate(); }
            catch (InvalidOperationException) { if (running()) throw; }
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (!running()) return;
                await pause().ConfigureAwait(false);
            }
            if (running()) throw new IOException("Epic did not exit after restart was requested. Check Windows permissions and try again.");
        }
        internal static async Task WaitForMainExitAsync(Func<Task<Win32Exception>> requestClose, Func<bool> running,
            Func<Task> pause, int attempts)
        {
            Win32Exception closeError = null;
            // Native RmShutdown can remain blocked. Never await it before starting
            // the bounded main-process wait.
            var closing = requestClose();
            // A shutdown status can report failure even when the target has
            // exited. Decide from the captured process lifetime before reopening.
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (closing.IsCompleted) closeError = await closing.ConfigureAwait(false);
                if (!running()) return;
                await pause().ConfigureAwait(false);
            }
            if (running()) throw new LauncherStillRunningException(closeError);
        }
        private static Process[] CurrentProcesses(string name)
        {
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            var found = Process.GetProcessesByName(name);
            var ours = new System.Collections.Generic.List<Process>();
            foreach (var process in found)
            {
                try
                {
                    if (!process.HasExited && process.SessionId == session) { ours.Add(process); continue; }
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            return ours.ToArray();
        }
        private static string ExecutablePath(int id, int? expectedSession = null)
        {
            using (var query = new System.Management.ManagementObjectSearcher(
                "SELECT ExecutablePath, SessionId FROM Win32_Process WHERE ProcessId=" + id))
            {
                query.Options.Timeout = TimeSpan.FromSeconds(2);
                using (var rows = query.Get())
                foreach (System.Management.ManagementObject row in rows)
                using (row)
                {
                    if (expectedSession.HasValue && Convert.ToInt32(row["SessionId"]) != expectedSession.Value) return null;
                    return row["ExecutablePath"] as string;
                }
            }
            return null;
        }
        private static Task<Win32Exception> RequestNormalExitAsync(Process process)
        {
            // Capture the exact lifetime before the caller can dispose Process.
            var identity = ProcessIdentity(process);
            string key = identity.Id + ":" + identity.Started.dwHighDateTime + ":" + identity.Started.dwLowDateTime;
            Task<Win32Exception> task;
            lock (shutdownGate)
            {
                if (pendingShutdowns.TryGetValue(key, out task)) return task;
                task = Task.Run(() => {
                    try { RequestNormalExit(identity); return (Win32Exception)null; }
                    catch (Win32Exception error) { return error; }
                });
                pendingShutdowns.Add(key, task);
            }
            task.ContinueWith(completed => {
                // Observe unexpected failures even if the caller timed out.
                var failure = completed.Exception;
                lock (shutdownGate)
                {
                    Task<Win32Exception> current;
                    if (pendingShutdowns.TryGetValue(key, out current) && current == completed)
                        pendingShutdowns.Remove(key);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
        private static UniqueProcess ProcessIdentity(Process process)
        {
            long started = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            return new UniqueProcess { Id = process.Id,
                Started = new System.Runtime.InteropServices.ComTypes.FILETIME {
                    dwLowDateTime = unchecked((int)started), dwHighDateTime = unchecked((int)(started >> 32)) } };
        }
        private static void ShutdownNormally(Process process) { RequestNormalExit(ProcessIdentity(process)); }
        private static void RequestNormalExit(UniqueProcess identity)
        {
            uint session;
            Check(RmStartSession(out session, 0, new StringBuilder(33)));
            try
            {
                Check(RmRegisterResources(session, 0, null, 1, new[] { identity }, 0, null));
                // No RmForceShutdown, file registrations, service registrations, or Kill.
                Check(RmShutdown(session, 0, IntPtr.Zero));
            }
            finally { RmEndSession(session); }
        }
        private static void Check(int code)
        {
            if (code != 0) throw new Win32Exception(code, "The launcher did not close normally. Exit it manually from the system tray.");
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess { public int Id; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint handle, uint flags, StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint handle, uint fileCount, string[] files,
            uint processCount, UniqueProcess[] processes, uint serviceCount, string[] services);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmShutdown(uint handle, uint flags, IntPtr callback);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint handle);
    }
}
