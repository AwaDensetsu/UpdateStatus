using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.UI
{
    public static class FrontendVisibility
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        public static string RestoreIfSteamForeground(Window window, uint appId, UpdateState state, ForegroundRestorePolicy policy)
        { return RestoreForLauncher(window,state,policy,new[] { "steam", "steamwebhelper" },() => SteamLocator.IsAppRunning(appId)); }
        public static string RestoreForLauncher(Window window, UpdateState state, ForegroundRestorePolicy policy, string[] clientProcesses, Func<bool> isGameRunning)
        {
            if (window == null) return null;
            bool gameRunning = isGameRunning != null && isGameRunning();
            if (gameRunning || state.Phase != UpdatePhase.Updating) { policy.Reset(); return null; }
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    string name = process.ProcessName;
                    bool client = false;
                    if (clientProcesses != null) foreach (string candidate in clientProcesses) client |= name.Equals(candidate,StringComparison.OrdinalIgnoreCase);
                    ForegroundOwner owner;
                    using (var current = Process.GetCurrentProcess()) owner = pid == current.Id ? ForegroundOwner.Frontend : client ? ForegroundOwner.Launcher : ForegroundOwner.Other;
                    if (!policy.ShouldAttempt(state, owner, false, DateTime.UtcNow)) return null;
                }
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                bool accepted = SetForegroundWindow(new WindowInteropHelper(window).Handle);
                policy.RecordResult(accepted);
                return accepted ? "Windows accepted frontend restoration" : "Windows refused frontend restoration (foreground lock)";
                // Never use Topmost, AttachThreadInput, Focus(), or minimize Steam. Windows
                // foreground-lock policy can refuse this request; don't fight the user's apps.
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return "Frontend restoration could not query the foreground process";
        }
    }
}
