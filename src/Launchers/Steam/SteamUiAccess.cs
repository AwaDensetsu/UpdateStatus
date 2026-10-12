using System;
using System.IO;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public static class SteamUiAccess
    {
        private static readonly object Gate = new object();
        private static string connectedRoot, connectedSession;
        private static DateTime connectedAt;
        private const string Marker = ".cef-enable-remote-debugging";

        public static bool Configured(string root)
        {
            return SteamLocator.IsValidRoot(root) &&
                (File.Exists(Path.Combine(root, Marker)) || Directory.Exists(Path.Combine(root, Marker)));
        }

        public static bool Connected(string root)
        {
            lock (Gate) return string.Equals(root, connectedRoot, StringComparison.OrdinalIgnoreCase) &&
                connectedSession == SteamLocator.SessionId() && DateTime.UtcNow - connectedAt < TimeSpan.FromSeconds(5);
        }

        public static void ObserveConnection(string root)
        {
            lock (Gate) { connectedRoot = root; connectedSession = SteamLocator.SessionId(); connectedAt = DateTime.UtcNow; }
        }

        public static void Disable(string root) { new SteamConnectionSetup(root).Remove(); }

        public static bool CanConnect(string root) { return Configured(root) || Connected(root); }

        // Called only by the explicit settings button. Existing markers are preserved.
        public static void Enable(string root)
        {
            if (!SteamLocator.IsValidRoot(root)) throw new ArgumentException("Select a valid Steam installation folder first.");
            if (Configured(root) || Connected(root)) return;
            new SteamConnectionSetup(root).Enable();
        }
    }
}
