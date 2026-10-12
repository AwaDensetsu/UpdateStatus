using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public static class SteamLocalAccessSetup
    {
        // Only an explicit settings-button click calls this method. Never elevate Playnite or Steam.
        public static Task EnableAsync(string root)
        {
            return Task.Run(() => Enable(root, folder => new SteamConnectionSetup(folder).Enable(), RequestAdministratorAccess));
        }

        public static Task DisableAsync(string root)
        {
            return Task.Run(() => {
                if (!SteamLocator.IsValidRoot(root)) throw new ArgumentException("Select a valid Steam installation folder first.");
                root = SteamLocator.NormalizeRoot(root);
                try { new SteamConnectionSetup(root).Remove(); }
                catch (UnauthorizedAccessException) { RequestAdministratorAccess(root, "--disable-local-access"); }
                // A pre-existing or changed marker is deliberately retained. The view model
                // still records the user's opt-out and stops this plugin's observation.
            });
        }

        internal static void Enable(string root, Action<string> writeMarker, Action<string> elevate)
        {
            if (!SteamLocator.IsValidRoot(root)) throw new ArgumentException("Select a valid Steam installation folder first.");
            root = SteamLocator.NormalizeRoot(root);
            if (SteamUiAccess.Configured(root)) return;
            try { writeMarker(root); }
            catch (UnauthorizedAccessException) { elevate(root); }
            if (!SteamUiAccess.Configured(root))
                throw new IOException("Local access could not be enabled. Check the Steam folder permissions.");
        }

        private static void RequestAdministratorAccess(string root) { RequestAdministratorAccess(root, "--enable-local-access"); }

        private static void RequestAdministratorAccess(string root, string action)
        {
            string helper = Path.Combine(Path.GetDirectoryName(typeof(SteamLocalAccessSetup).Assembly.Location), "SteamUpdateReader.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("The local access setup helper is missing. Reinstall Update Status.", helper);
            var start = new ProcessStartInfo(helper, SteamLocator.QuoteRoot(root) + " " + action + " " + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using (var process = Process.Start(start))
            {
                if (process == null || !process.WaitForExit(30000)) throw new IOException("Local access setup did not finish. Check the Steam folder before trying again.");
                if (process.ExitCode != 0) throw new IOException("Local access setup failed. Check the Steam folder permissions.");
            }
        }
    }
}
