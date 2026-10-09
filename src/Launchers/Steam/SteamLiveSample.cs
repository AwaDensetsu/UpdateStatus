using System;
using System.Globalization;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // The S3 frame contains the reviewed 248-byte update layout plus sanitized metadata.
    public sealed class SteamLiveSample
    {
        public uint AppId, InstallState, UpdateState, SourceBuild, TargetBuild, Result;
        public ulong Downloaded, DownloadTotal, Staged, StageTotal;
        public bool HasCounters;
        public bool? DownloadsEnabled;
        public uint? NetworkBytesPerSecond;
        public uint NetworkAppId;
        public int SecondsRemaining;
        public DateTime ReceivedAtUtc;
        public SteamStageProgress[] Progress;
        public static SteamLiveSample Parse(string line, uint expectedAppId)
        {
            if (line == null || line.Length > 1024) throw new FormatException("Invalid Steam reader frame size");
            string[] parts = line.Split('|'); uint id, state;
            if (parts.Length != 4 || parts[0] != "S3" || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id != expectedAppId ||
                !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out state)) throw new FormatException("Invalid Steam reader identity/frame");
            var sample = new SteamLiveSample { AppId = id, InstallState = state };
            if (parts[3] == "-") return sample;
            if (parts[3].Length != 496) throw new FormatException("Unexpected Steam update struct size");
            byte[] data = new byte[248];
            for (int i = 0; i < data.Length; i++) data[i] = byte.Parse(parts[3].Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            uint downloads = BitConverter.ToUInt32(data,0);
            if (downloads > 2) throw new FormatException("Invalid Steam downloads control metadata");
            sample.DownloadsEnabled = downloads == 0 ? (bool?)null : downloads == 1;
            uint network = BitConverter.ToUInt32(data,12);
            sample.NetworkAppId = BitConverter.ToUInt32(data,20);
            uint speed = BitConverter.ToUInt32(data,16);
            if (network > 1 || network == 1 && sample.NetworkAppId == 0 || network == 0 && (speed != 0 || sample.NetworkAppId != 0))
                throw new FormatException("Invalid Steam network metadata");
            sample.NetworkBytesPerSecond = network == 1 ? (uint?)speed : null;
            sample.UpdateState = BitConverter.ToUInt32(data, 4);
            sample.SecondsRemaining = BitConverter.ToInt32(data, 8);
            sample.SourceBuild = BitConverter.ToUInt32(data, 32); sample.TargetBuild = BitConverter.ToUInt32(data, 36); sample.Result = BitConverter.ToUInt32(data, 60);
            sample.Downloaded = BitConverter.ToUInt64(data, 128); sample.DownloadTotal = BitConverter.ToUInt64(data, 136);
            sample.Staged = BitConverter.ToUInt64(data, 152); sample.StageTotal = BitConverter.ToUInt64(data, 160);
            if (sample.Downloaded > sample.DownloadTotal || sample.Staged > sample.StageTotal) throw new FormatException("Invalid Steam live byte counters");
            // These redundant IPC fields agree in the verified downloading/staging layout.
            // Other phases are allowed to expose different processing totals; they remain
            // numeric-unknown if all counted download/stage work is complete.
            sample.HasCounters = true;
            sample.Progress = new SteamStageProgress[7];
            for (int i = 0; i < 7; i++)
            {
                int offset = 80 + i * 24;
                var stage = new SteamStageProgress { Done = BitConverter.ToUInt64(data, offset), Total = BitConverter.ToUInt64(data, offset + 8), Weight = BitConverter.ToSingle(data, offset + 20) };
                if (stage.Done > stage.Total || float.IsNaN(stage.Weight) || float.IsInfinity(stage.Weight) || stage.Weight < 0)
                    throw new FormatException("Invalid Steam stage progress");
                sample.Progress[i] = stage;
            }
            return sample;
        }
        public SteamManifest ToManifest()
        {
            return new SteamManifest { AppId = AppId, Flags = InstallState,
                Downloaded = HasCounters ? (ulong?)Downloaded : null, DownloadTotal = HasCounters ? (ulong?)DownloadTotal : null,
                Staged = HasCounters ? (ulong?)Staged : null, StageTotal = HasCounters ? (ulong?)StageTotal : null,
                // SourceBuild is the pre-update build, not the currently installed build.
                // Live install flags provide completion authority; don't compare it to target.
                BuildId = null, TargetBuildId = TargetBuild == 0 ? (ulong?)null : TargetBuild, UpdateResult = Result };
        }
    }
}
