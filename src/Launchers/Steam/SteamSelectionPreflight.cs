using System;
using System.IO;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public static class SteamSelectionPreflight
    {
        public static bool ShouldDefer(LaunchIdentity identity, SteamCatalogSnapshot snapshot)
        {
            uint appId = uint.Parse(identity.LauncherId);
            var entry = snapshot.Find(appId);
            // Fresh native state outranks a saved manifest. Optional updates do not
            // block Play. Discovery itself never starts Steam/downloads.
            if (snapshot.Fresh && entry != null)
            {
                if (entry.IsRunning || entry.Status == SteamCatalogStatus.UpToDate || entry.Status == SteamCatalogStatus.UpdateAvailable) return false;
            }
            try
            {
                var locator = new SteamLocator(); string root = locator.FindRoot();
                if (root == null || !SteamUpdateControl.SupportsSelection(root)) return false;
                if (snapshot.Fresh && entry != null && entry.Status != SteamCatalogStatus.Unknown)
                    return entry.RequiresUpdate == true || entry.Status == SteamCatalogStatus.Queued || entry.Status == SteamCatalogStatus.Updating || entry.Status == SteamCatalogStatus.RepairRequired;
                string path = locator.FindManifest(root,appId,identity.InstallDirectory);
                if (path == null) return false;
                var manifest = SteamManifestParser.Parse(SteamManifestParser.ReadShared(path),appId);
                return manifest.Flags.HasValue && manifest.Flags <= uint.MaxValue && SteamSelectionPolicy.Pending((uint)manifest.Flags.Value);
            }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (FormatException) { return false; }
        }
    }
}
