using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using System;

namespace PlayniteUpdateStatus.Launchers
{
    // A provider enters this runtime catalog only after real update-source validation.
    // Planned launchers are documented separately, never registered as fake adapters.
    public static class LauncherCatalog
    {
        public static LauncherRegistry Create(Action<string,bool> log)
        {
            var registry = new LauncherRegistry();
            registry.Register(new LauncherRegistration("steam",new SteamUpdateAdapter(log),SteamGameIdentity.Resolve,
                new[] { "steam", "steamwebhelper" },identity => SteamLocator.IsAppRunning(uint.Parse(identity.LauncherId)),
                UpdateCapabilities.Phases | UpdateCapabilities.DownloadBytes | UpdateCapabilities.WorkProgress | UpdateCapabilities.RemainingTime | UpdateCapabilities.PauseResume));
            return registry;
        }
    }
}
