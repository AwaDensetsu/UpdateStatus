using System;
using System.Globalization;
using System.IO;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Steam's private format is not an API contract. Unknown bits suppress numeric progress.
    public sealed class SteamManifest
    {
        public uint AppId;
        public ulong? Flags, DownloadTotal, Downloaded, StageTotal, Staged, SizeOnDisk, BuildId, TargetBuildId, LastUpdated, UpdateResult;
        public string InstallDir, Name;
        public const ulong FullyInstalled = 4, Running = 8192;
        public const ulong UpdateMask = 2 | 32 | 128 | 256 | 512 | 1024 | 65536 | 131072 | 262144 | 524288 | 1048576 | 2097152 | 4194304 | 8388608;
        public const ulong ActiveMask = 256 | 1024 | 65536 | 131072 | 262144 | 524288 | 1048576 | 2097152 | 4194304 | 8388608;
        public bool Updating { get { return Flags.HasValue && (Flags.Value & UpdateMask) != 0; } }
        public bool Active { get { return Flags.HasValue && (Flags.Value & ActiveMask) != 0; } }
        public bool Installed { get { return Flags.HasValue && (Flags.Value & FullyInstalled) != 0 && !Updating && (Flags.Value & (2048 | 4096 | 32768)) == 0; } }
        public bool IsRunning { get { return Flags.HasValue && (Flags.Value & Running) != 0; } }
        public bool KnownFlags { get { return Flags.HasValue && (Flags.Value & ~(UpdateMask | FullyInstalled | Running | 1 | 8 | 16 | 64 | 2048 | 4096 | 16384 | 32768)) == 0; } }
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "AppID={0}; Flags={1}; download={2}/{3}; stage={4}/{5}; build={6}; target={7}; last={8}; result={9}; size={10}",
                AppId, Flags, Downloaded, DownloadTotal, Staged, StageTotal, BuildId, TargetBuildId, LastUpdated, UpdateResult, SizeOnDisk);
        }
    }

    public static class SteamManifestParser
    {
        public static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 4 * 1024 * 1024) throw new FormatException("Manifest too large");
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
        public static SteamManifest Parse(string text, uint expectedAppId)
        {
            var root = KeyValues.Parse(text).Child("AppState");
            if (root == null) throw new FormatException("Missing AppState");
            ulong? appId = Number(root, "appid");
            if (!appId.HasValue || appId == 0 || appId > uint.MaxValue || (expectedAppId != 0 && appId != expectedAppId))
                throw new FormatException("Missing/mismatched AppID");
            return new SteamManifest { AppId = (uint)appId.Value, Flags = Number(root, "StateFlags"),
                DownloadTotal = Number(root, "BytesToDownload"), Downloaded = Number(root, "BytesDownloaded"),
                StageTotal = Number(root, "BytesToStage"), Staged = Number(root, "BytesStaged"),
                SizeOnDisk = Number(root, "SizeOnDisk"), BuildId = Number(root, "buildid"), TargetBuildId = Number(root, "TargetBuildID"),
                LastUpdated = Number(root, "LastUpdated"), UpdateResult = Number(root, "UpdateResult"), InstallDir = root.Get("installdir"), Name = root.Get("name") };
        }
        private static ulong? Number(KvNode node, string key)
        {
            string value = node.Get(key);
            if (value == null) return null;
            ulong number;
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)) throw new FormatException("Invalid " + key);
            return number;
        }
    }
}
