using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus
{
    // Persisted preferences only. Keep these names/defaults compatible with existing installs.
    public sealed class Settings
    {
        public bool Enabled { get; set; } = true;
        public string SteamFolder { get; set; } = "";
        public int PollingInterval { get; set; } = 1000;
        public bool KeepFrontendVisible { get; set; } = true;
        public bool AutomaticOverlay { get; set; } = true;
        public bool CoverBadges { get; set; } = true;
        public SpeedUnit SpeedUnit { get; set; }
        public ExitDownloadAction ExitAction { get; set; }

        internal void Copy(Settings other)
        {
            Enabled = other.Enabled;
            SteamFolder = other.SteamFolder ?? "";
            PollingInterval = other.PollingInterval;
            KeepFrontendVisible = other.KeepFrontendVisible;
            AutomaticOverlay = other.AutomaticOverlay;
            CoverBadges = other.CoverBadges;
            SpeedUnit = other.SpeedUnit;
            ExitAction = other.ExitAction;
        }
    }
}
