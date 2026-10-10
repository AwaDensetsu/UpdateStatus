using System;
using NUnit.Framework;
using PlayniteUpdateStatus.Launchers.Steam;
using static UpdateStatus.Tests.Support.SteamSamples;
using static UpdateStatus.Tests.Unit.UnitAssertions;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("TransferMetrics")]
    public sealed class TransferMetricsTests
    {
        [Test]
        [Description("native NETWORK rate displays immediately and never averages byte changes")]
        public void NativeNETWORKRateDisplaysImmediatelyAndNeverAveragesByteChanges()
        {
            var rate = new SteamTransferMetrics();
            var s = RateSample(DateTime.UtcNow, 100);
            s.NetworkBytesPerSecond = 512000;
            s.NetworkAppId = 252490;
            var d = SteamLivePresentation.Build(s);
            rate.Apply(s, d);
            Eq((double?)512000, d.BytesPerSecond);
            s.Downloaded = 200;
            s.NetworkBytesPerSecond = 5242880;
            d = SteamLivePresentation.Build(s);
            rate.Apply(s, d);
            Eq((double?)5242880, d.BytesPerSecond);
            s.NetworkBytesPerSecond = 0;
            rate.Apply(s, d);
            Eq((double?)0, d.BytesPerSecond);
            s.NetworkBytesPerSecond = null;
            rate.Apply(s, d);
            Eq((double?)null, d.BytesPerSecond);
        }

        [Test]
        [Description("legacy helper and paused or missing data cannot supply derived NETWORK speed")]
        public void LegacyHelperAndPausedOrMissingDataCannotSupplyDerivedNETWORKSpeed()
        {
            var rate = new SteamTransferMetrics();
            var time = DateTime.UtcNow;
            var s = RateSample(time, 100);
            rate.Apply(s, SteamLivePresentation.Build(s));
            s = RateSample(time.AddSeconds(1), 10000);
            var d = SteamLivePresentation.Build(s);
            rate.Apply(s, d);
            Eq((double?)null, d.BytesPerSecond);
            s.NetworkBytesPerSecond = 512000;
            s.DownloadsEnabled = false;
            rate.Apply(s, d);
            Eq((double?)null, d.BytesPerSecond);
            s.DownloadsEnabled = true;
            d.Paused = true;
            rate.Apply(s, d);
            Eq((double?)null, d.BytesPerSecond);
            d.Paused = false;
            d.WaitingForData = true;
            rate.Apply(s, d);
            Eq((double?)null, d.BytesPerSecond);
            d.WaitingForData = false;
            s.ReceivedAtUtc = DateTime.MinValue;
            rate.Apply(s, d);
            Eq((double?)null, d.BytesPerSecond);
        }
    }
}
