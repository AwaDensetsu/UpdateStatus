using System;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Support
{
    internal static class SteamSamples
    {
        internal static SteamManifest M(ulong flags, ulong dt = 100, ulong d = 0, ulong st = 0, ulong s = 0)
        {
            return SteamManifestParser.Parse(Fixture(flags, dt, d, st, s), 550);
        }

        internal static string Fixture(ulong flags, ulong dt, ulong d, ulong st, ulong s)
        {
            return "\"AppState\" { \"appid\" \"550\" \"StateFlags\" \"" + flags + "\" \"BytesToDownload\" \"" + dt + "\" \"BytesDownloaded\" \"" + d + "\" \"BytesToStage\" \"" + st + "\" \"BytesStaged\" \"" + s + "\" \"buildid\" \"10\" \"TargetBuildID\" \"10\" }";
        }

        internal static UpdateObservation Eval(SteamProgress p, SteamManifest m)
        {
            return p.Evaluate(m, false, true, null);
        }

        internal static UpdateObservation LiveEval(SteamProgress p, SteamLiveSample s)
        {
            var o = p.Evaluate(s.ToManifest(), s.UpdateState != 0, s.HasCounters, null);
            if (o.Phase == UpdatePhase.Updating && !o.CompletionPending)
            {
                o.Display = SteamLivePresentation.Build(s);
                o.Percent = o.Display.WorkPercent;
            }

            return o;
        }

        internal static void Bad(string text)
        {
            try
            {
                SteamManifestParser.Parse(text, 550);
            }
            catch (FormatException)
            {
                return;
            }

            throw new Exception("Malformed manifest accepted");
        }

        internal static string LiveFrame(uint id, uint flags, ulong done, ulong total, ulong staged, ulong stageTotal)
        {
            return new SteamLiveSample
            {
                AppId = id,
                InstallState = flags,
                UpdateState = 97,
                SourceBuild = 10,
                TargetBuild = 10,
                HasCounters = true,
                Downloaded = done,
                DownloadTotal = total,
                Staged = staged,
                StageTotal = stageTotal,
                UiWorkPercent = SteamLivePresentation.Ratio(staged, stageTotal)
            }.Frame();
        }

        internal static void Put(byte[] target, int offset, byte[] value)
        {
            Array.Copy(value, 0, target, offset, value.Length);
        }

        internal static string AlterLive(string frame, int offset, byte[] value)
        {
            var sample = SteamJson.Decode<SteamLiveSample>(frame.Split('|')[3]);
            switch (offset)
            {
                case 0:
                    uint enabled = BitConverter.ToUInt32(value, 0);
                    if (enabled > 2)
                        return "S4|" + sample.AppId + "|" + sample.InstallState + "|bad";
                    sample.DownloadsEnabled = enabled == 0 ? (bool?)null : enabled == 1;
                    break;
                case 4:
                    sample.UpdateState = BitConverter.ToUInt32(value, 0);
                    break;
                case 8:
                    sample.SecondsRemaining = BitConverter.ToInt32(value, 0);
                    break;
                case 12:
                    uint known = BitConverter.ToUInt32(value, 0);
                    if (known > 1 || known == 0 && sample.NetworkBytesPerSecond > 0)
                        return "S4|" + sample.AppId + "|" + sample.InstallState + "|bad";
                    sample.NetworkBytesPerSecond = known == 1 ? (uint?)0 : null;
                    break;
                case 16:
                    sample.NetworkBytesPerSecond = BitConverter.ToUInt32(value, 0);
                    break;
                case 20:
                    sample.NetworkAppId = BitConverter.ToUInt32(value, 0);
                    break;
                default:
                    throw new ArgumentException("Obsolete binary fixture offset");
            }

            return sample.Frame();
        }

        internal static void BadLive(string line)
        {
            try
            {
                SteamLiveSample.Parse(line, 550);
            }
            catch (FormatException)
            {
                return;
            }

            throw new Exception("Invalid live frame accepted");
        }

        internal static SteamLiveSample RateSample(DateTime time, ulong bytes)
        {
            return new SteamLiveSample
            {
                AppId = 550,
                TargetBuild = 10,
                UpdateState = 97,
                InstallState = 1294,
                Downloaded = bytes,
                DownloadTotal = 100000,
                HasCounters = true,
                ReceivedAtUtc = time,
                UiWorkPercent = 50
            };
        }
    }
}
