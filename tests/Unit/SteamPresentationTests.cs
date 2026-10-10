using System.Threading;
using NUnit.Framework;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using static UpdateStatus.Tests.Support.SteamSamples;
using static UpdateStatus.Tests.Unit.UnitAssertions;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("SteamPresentation")]
    public sealed class SteamPresentationTests
    {
        [Test]
        [Description("live unavailable counters yields a calm waiting display")]
        public void LiveUnavailableCountersYieldsACalmWaitingDisplay()
        {
            var s = SteamLiveSample.Parse("S4|550|1294|-", 550);
            Eq(false, s.HasCounters);
            Eq((int?)null, Eval(new SteamProgress(), s.ToManifest()).Percent);
            Eq(true, SteamLivePresentation.Build(s).WaitingForData);
        }

        [Test]
        [Description("large unsigned download bytes don't overflow")]
        public void LargeUnsignedDownloadBytesDonTOverflow()
        {
            Eq((int?)50, SteamLivePresentation.Ratio((ulong.MaxValue - 1) / 2, ulong.MaxValue - 1));
        }

        [Test]
        [Description("counter exceeds total is unknown")]
        public void CounterExceedsTotalIsUnknown()
        {
            Eq((int?)null, SteamLivePresentation.Ratio(101, 100));
        }

        [Test]
        [Description("zero denominator is unknown")]
        public void ZeroDenominatorIsUnknown()
        {
            Eq((int?)null, SteamLivePresentation.Ratio(0, 0));
        }

        [Test]
        [Description("unknown native update state hides numbers")]
        public void UnknownNativeUpdateStateHidesNumbers()
        {
            var s = SteamLiveSample.Parse(LiveFrame(550, 1294, 50, 100, 70, 100), 550);
            s.UpdateState = 4097;
            var display = SteamLivePresentation.Build(s);
            Eq((int?)null, display.WorkPercent);
            Eq((ulong?)null, display.Downloaded);
        }

        [Test]
        [Description("native stage priority and install/update distinction")]
        public void NativeStagePriorityAndInstallUpdateDistinction()
        {
            Eq("Verifying files", SteamLivePresentation.Stage(1 | 128 | 32, 4));
            Eq("Installing files", SteamLivePresentation.Stage(1 | 256 | 128, 4));
            Eq("Reserving space", SteamLivePresentation.Stage(1 | 16, 4));
            Eq("Finalizing", SteamLivePresentation.Stage(1 | 512, 4));
            Eq("Installing files", SteamLivePresentation.Stage(97, 0));
            Eq("Patching files", SteamLivePresentation.Stage(65, 4));
            Eq("Unpacking files", SteamLivePresentation.Stage(65, 0));
            Eq("Stopping", SteamLivePresentation.Stage(1025, 4));
        }

        [Test]
        [Description("Greek byte units match Steam 5 GB total and comma decimals")]
        public void GreekByteUnitsMatchSteam5GBTotalAndCommaDecimals()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("el-GR");
                var d = new UpdateDisplay
                {
                    Downloaded = 3354187488,
                    DownloadTotal = 5362895728,
                    Stage = "Updating files",
                    WorkPercent = 93
                };
                Eq("Downloading data — 3,1 GB / 5,0 GB", d.DownloadText);
                Eq("Updating files — 93%", d.StageText);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = old;
            }
        }

        [Test]
        [Description("waiting and pause never show outdated throughput/ETA")]
        public void WaitingAndPauseNeverShowOutdatedThroughputETA()
        {
            var d = new UpdateDisplay
            {
                BytesPerSecond = 5000000,
                SecondsRemaining = 600,
                Paused = true
            };
            Eq("Download paused", d.MetricsText);
            d.WaitingForData = true;
            Eq("Waiting for update data…", d.MetricsText);
        }
    }
}
