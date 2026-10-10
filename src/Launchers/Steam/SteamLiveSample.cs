using System;
using System.Globalization;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamLiveSample
    {
        public uint AppId, InstallState, UpdateState, SourceBuild, TargetBuild, Result;
        public ulong Downloaded, DownloadTotal, Staged, StageTotal;
        public bool HasCounters;
        public bool? DownloadsEnabled;
        public uint? NetworkBytesPerSecond;
        public uint NetworkAppId;
        public int SecondsRemaining = -1;
        public int? UiWorkPercent;
        public DateTime ReceivedAtUtc;

        public string Payload() { return SteamJson.Encode(this); }
        public string Frame() { return "S4|" + AppId + "|" + InstallState + "|" + Payload(); }

        public static SteamLiveSample Parse(string line, uint expectedAppId)
        {
            if (line == null || line.Length > 65536) throw new FormatException("Invalid Steam reader frame size");
            string[] parts = line.Split('|');
            uint id, flags;
            if (parts.Length != 4 || parts[0] != "S4" ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0 || id != expectedAppId ||
                !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out flags))
                throw new FormatException("Invalid Steam reader identity/frame");
            if (parts[3] == "-") return new SteamLiveSample { AppId = id, InstallState = flags };
            var sample = SteamJson.Decode<SteamLiveSample>(parts[3]);
            if (sample == null || sample.AppId != id || sample.InstallState != flags ||
                sample.Downloaded > sample.DownloadTotal || sample.Staged > sample.StageTotal ||
                sample.UiWorkPercent < 0 || sample.UiWorkPercent > 100 ||
                (sample.NetworkBytesPerSecond.HasValue ? sample.NetworkAppId != id : sample.NetworkAppId != 0))
                throw new FormatException("Invalid Steam UI counters or identity");
            sample.ReceivedAtUtc = DateTime.MinValue;
            return sample;
        }

        public SteamManifest ToManifest()
        {
            return new SteamManifest {
                AppId = AppId, Flags = InstallState,
                Downloaded = HasCounters ? (ulong?)Downloaded : null,
                DownloadTotal = HasCounters ? (ulong?)DownloadTotal : null,
                Staged = HasCounters ? (ulong?)Staged : null,
                StageTotal = HasCounters ? (ulong?)StageTotal : null,
                BuildId = null, TargetBuildId = TargetBuild == 0 ? (ulong?)null : TargetBuild,
                UpdateResult = Result
            };
        }
    }
}
