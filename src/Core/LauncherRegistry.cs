using System;
using System.Collections.Generic;
using Playnite.SDK.Models;

namespace PlayniteUpdateStatus.Core
{
    [Flags]
    public enum UpdateCapabilities { None = 0, Phases = 1, DownloadBytes = 2, WorkProgress = 4, RemainingTime = 8, PauseResume = 16 }

    public sealed class LauncherRegistration
    {
        public string Key { get; private set; }
        public ILauncherUpdateAdapter Adapter { get; private set; }
        public Func<Game,LaunchIdentity> ResolveIdentity { get; private set; }
        public Func<LaunchIdentity,bool> IsGameRunning { get; private set; }
        public string[] ClientProcesses { get; private set; }
        public UpdateCapabilities Capabilities { get; private set; }
        public LauncherRegistration(string key, ILauncherUpdateAdapter adapter, Func<Game,LaunchIdentity> resolver,
            string[] clientProcesses, Func<LaunchIdentity,bool> isGameRunning, UpdateCapabilities capabilities)
        {
            if (string.IsNullOrWhiteSpace(key) || adapter == null || resolver == null) throw new ArgumentException("Launcher registration requires a key, adapter and identity resolver");
            Key = key; Adapter = adapter; ResolveIdentity = resolver; ClientProcesses = clientProcesses == null ? new string[0] : (string[])clientProcesses.Clone(); IsGameRunning = isGameRunning; Capabilities = capabilities;
        }
    }
    public sealed class LauncherMatch
    {
        public LauncherRegistration Registration { get; private set; }
        public LaunchIdentity Identity { get; private set; }
        public LauncherMatch(LauncherRegistration registration, LaunchIdentity identity) { Registration = registration; Identity = identity; }
    }
    public sealed class LauncherRegistry
    {
        private readonly List<LauncherRegistration> entries = new List<LauncherRegistration>();
        public IEnumerable<LauncherRegistration> Registrations { get { return entries.AsReadOnly(); } }
        public void Register(LauncherRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException("registration");
            foreach (var existing in entries) if (existing.Key.Equals(registration.Key,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Duplicate launcher key: " + registration.Key);
            entries.Add(registration);
        }
        public LauncherMatch Resolve(Game game, Func<LauncherRegistration,bool> enabled)
        {
            LauncherMatch result = null;
            foreach (var entry in entries)
            {
                if (enabled != null && !enabled(entry)) continue;
                var identity = entry.ResolveIdentity(game);
                if (identity == null) continue;
                // Ambiguous mappings must never observe a different game's update.
                if (result != null) return null;
                identity.LauncherKey = entry.Key; identity.LauncherName = entry.Adapter.Name;
                result = new LauncherMatch(entry,identity);
            }
            return result;
        }
    }
}
