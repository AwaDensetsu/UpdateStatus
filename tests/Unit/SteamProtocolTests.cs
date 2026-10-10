using System;
using NUnit.Framework;
using PlayniteUpdateStatus.Launchers.Steam;
using static UpdateStatus.Tests.Support.SteamSamples;
using static UpdateStatus.Tests.Unit.UnitAssertions;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("SteamProtocol")]
    public sealed class SteamProtocolTests
    {
        [Test]
        [Description("live frame mismatched appid rejected")]
        public void LiveFrameMismatchedAppidRejected()
        {
            BadLive(LiveFrame(551, 1294, 50, 100, 70, 100));
        }

        [Test]
        [Description("live frame wrong size rejected")]
        public void LiveFrameWrongSizeRejected()
        {
            BadLive("S4|550|1294|00");
        }

        [Test]
        [Description("live frame counter overflow rejected")]
        public void LiveFrameCounterOverflowRejected()
        {
            BadLive(LiveFrame(550, 1294, 101, 100, 70, 100));
        }

        [Test]
        [Description("live frame malformed payload rejected")]
        public void LiveFrameMalformedPayloadRejected()
        {
            BadLive("S4|550|1294|" + new string('Z', 496));
        }

        [Test]
        [Description("live frame negative flags rejected")]
        public void LiveFrameNegativeFlagsRejected()
        {
            BadLive("S4|550|-1|-");
        }

        [Test]
        [Description("old helper protocol is rejected")]
        public void OldHelperProtocolIsRejected()
        {
            BadLive(LiveFrame(550, 1294, 50, 100, 70, 100).Replace("S4|", "S1|"));
        }

        [Test]
        [Description("old S2 helper cannot supply a false zero ETA")]
        public void OldS2HelperCannotSupplyAFalseZeroETA()
        {
            BadLive(LiveFrame(550, 1294, 50, 100, 70, 100).Replace("S4|", "S2|"));
        }

        [Test]
        [Description("ETA is the native signed field and paused/unknown estimates are hidden")]
        public void ETAIsTheNativeSignedFieldAndPausedUnknownEstimatesAreHidden()
        {
            var frame = LiveFrame(550, 1294, 50, 100, 70, 100);
            var s = SteamLiveSample.Parse(AlterLive(frame, 8, BitConverter.GetBytes(3661)), 550);
            Eq((int?)3661, SteamLivePresentation.Build(s).SecondsRemaining);
            Eq("Estimated 1:01:01 remaining", SteamLivePresentation.Build(s).MetricsText);
            s.Result = 4;
            Eq((int?)null, SteamLivePresentation.Build(s).SecondsRemaining);
            s.Result = 0;
            s.SecondsRemaining = -1;
            Eq((int?)null, SteamLivePresentation.Build(s).SecondsRemaining);
        }

        [Test]
        [Description("NETWORK metadata separates native zero from unknown and validates identity")]
        public void NETWORKMetadataSeparatesNativeZeroFromUnknownAndValidatesIdentity()
        {
            var frame = LiveFrame(550, 1294, 50, 100, 70, 100);
            Eq((uint?)null, SteamLiveSample.Parse(frame, 550).NetworkBytesPerSecond);
            frame = AlterLive(AlterLive(frame, 20, BitConverter.GetBytes(550u)), 12, BitConverter.GetBytes(1u));
            var s = SteamLiveSample.Parse(frame, 550);
            Eq((uint?)0, s.NetworkBytesPerSecond);
            Eq(550u, s.NetworkAppId);
            BadLive(AlterLive(frame, 20, BitConverter.GetBytes(551u)));
            Eq((uint?)uint.MaxValue, SteamLiveSample.Parse(AlterLive(frame, 16, BitConverter.GetBytes(uint.MaxValue)), 550).NetworkBytesPerSecond);
            BadLive(AlterLive(frame, 12, BitConverter.GetBytes(2u)));
            BadLive(AlterLive(frame, 20, BitConverter.GetBytes(0u)));
            BadLive(AlterLive(frame, 12, BitConverter.GetBytes(0u)));
        }
    }
}
