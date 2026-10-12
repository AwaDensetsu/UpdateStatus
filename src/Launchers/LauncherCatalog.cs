using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.Launchers.Epic;
using System;

namespace PlayniteUpdateStatus.Launchers
{
    // Steam is the accepted provider. Epic is registered only by the isolated beta
    // composition root and advertises observation capabilities, never untested controls.
    public static class LauncherCatalog
    {
        public static LauncherRegistry Create(Action<string,bool> log, EpicUpdateCatalog epic = null)
        {
            var registry = new LauncherRegistry();
            registry.Register(new LauncherRegistration("steam",new SteamUpdateAdapter(log),SteamGameIdentity.Resolve,
                new[] { "steam", "steamwebhelper" },identity => SteamLocator.IsAppRunning(uint.Parse(identity.LauncherId)),
                UpdateCapabilities.Phases | UpdateCapabilities.DownloadBytes | UpdateCapabilities.WorkProgress | UpdateCapabilities.RemainingTime | UpdateCapabilities.PauseResume));
            if (epic != null)
                registry.Register(new LauncherRegistration("epic", new EpicUpdateAdapter(epic), EpicSnapshot.Resolve,
                    new[] { "EpicGamesLauncher", "EpicWebHelper" }, null, UpdateCapabilities.Phases));
            return registry;
        }
    }
}
