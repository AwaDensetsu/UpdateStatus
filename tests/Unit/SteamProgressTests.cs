using System;
using NUnit.Framework;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using static UpdateStatus.Tests.Support.SteamSamples;
using static UpdateStatus.Tests.Unit.UnitAssertions;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    [Category("SteamProgress")]
    public sealed class SteamProgressTests
    {
        [Test]
        [Description("no update with stale complete counters")]
        public void NoUpdateWithStaleCompleteCounters()
        {
            Eq(UpdatePhase.Launching, Eval(new SteamProgress(), M(4, 100, 100, 200, 200)).Phase);
        }

        [Test]
        [Description("UI frame separates download from file work")]
        public void UIFrameSeparatesDownloadFromFileWork()
        {
            var s = SteamLiveSample.Parse(LiveFrame(550, 1294, 50, 100, 70, 100), 550);
            var o = LiveEval(new SteamProgress(), s);
            Eq((ulong?)50, o.Display.Downloaded);
            Eq((int?)50, o.Display.DownloadPercent);
            Eq((int?)70, o.Percent);
            Eq("Updating files", o.Display.Stage);
        }

        [Test]
        [Description("live IPC advances independent bars while checkpoint stays zero")]
        public void LiveIPCAdvancesIndependentBarsWhileCheckpointStaysZero()
        {
            var p = new SteamProgress();
            var persisted = M(1030, 100, 0, 100, 0);
            var a = LiveEval(p, SteamLiveSample.Parse(LiveFrame(550, 1294, 40, 100, 60, 100), 550));
            var b = LiveEval(p, SteamLiveSample.Parse(LiveFrame(550, 1294, 50, 100, 70, 100), 550));
            Eq((int?)40, a.Display.DownloadPercent);
            Eq((int?)60, a.Percent);
            Eq((int?)50, b.Display.DownloadPercent);
            Eq((int?)70, b.Percent);
            Eq((ulong?)0, persisted.Downloaded);
        }

        [Test]
        [Description("fresh paused measurement retains bytes and labels pause")]
        public void FreshPausedMeasurementRetainsBytesAndLabelsPause()
        {
            var s = SteamLiveSample.Parse(LiveFrame(550, 1806, 50, 100, 70, 100), 550);
            var o = LiveEval(new SteamProgress(), s);
            Eq((int?)70, o.Percent);
            Eq("Paused", o.Display.Stage);
            Eq((ulong?)50, o.Display.Downloaded);
        }

        [Test]
        [Description("real suspended IPC state zero and result four is pause, not queue")]
        public void RealSuspendedIPCStateZeroAndResultFourIsPauseNotQueue()
        {
            var s = SteamLiveSample.Parse(LiveFrame(550, 1038, 50, 100, 70, 100), 550);
            s.UpdateState = 0;
            s.Result = 4;
            var o = LiveEval(new SteamProgress(), s);
            Eq("Paused", o.Display.Stage);
            Eq((int?)70, o.Percent);
        }

        [Test]
        [Description("live source does not claim 100 during final commit")]
        public void LiveSourceDoesNotClaim100DuringFinalCommit()
        {
            Eq((int?)null, Eval(new SteamProgress(), SteamLiveSample.Parse(LiveFrame(550, 4194564, 100, 100, 100, 100), 550).ToManifest()).Percent);
        }

        [Test]
        [Description("live running state terminates observation")]
        public void LiveRunningStateTerminatesObservation()
        {
            Eq(UpdatePhase.Running, Eval(new SteamProgress(), SteamLiveSample.Parse(LiveFrame(550, 8196, 100, 100, 100, 100), 550).ToManifest()).Phase);
        }

        [Test]
        [Description("native source build is not current installed build")]
        public void NativeSourceBuildIsNotCurrentInstalledBuild()
        {
            var p = new SteamProgress();
            Eval(p, SteamLiveSample.Parse(LiveFrame(550, 1294, 50, 100, 70, 100), 550).ToManifest());
            var s = SteamLiveSample.Parse(LiveFrame(550, 4, 100, 100, 100, 100), 550);
            s.SourceBuild = 10;
            s.TargetBuild = 20;
            Eq((ulong?)null, s.ToManifest().BuildId);
            Eval(p, s.ToManifest());
            Eq(UpdatePhase.Ready, Eval(p, s.ToManifest()).Phase);
        }

        [Test]
        [Description("live active update-state vetoes installed-flag completion")]
        public void LiveActiveUpdateStateVetoesInstalledFlagCompletion()
        {
            var p = new SteamProgress();
            var m = SteamLiveSample.Parse(LiveFrame(550, 4, 100, 100, 100, 100), 550).ToManifest();
            Eq(UpdatePhase.Updating, p.Evaluate(m, true, true, null).Phase);
            Eq((int?)null, p.Evaluate(m, true, true, null).Percent);
        }

        [Test]
        [Description("persisted initial zero is not live progress")]
        public void PersistedInitialZeroIsNotLiveProgress()
        {
            var f = new SteamCounterFreshness();
            var p = new SteamProgress();
            var now = new DateTime(2026, 10, 6);
            Eq((int?)null, p.Evaluate(M(1030), false, f.Observe(M(1030), now), null).Percent);
            Eq(false, f.Observe(M(1030), now.AddMinutes(2)));
        }

        [Test]
        [Description("pause checkpoint changes then expires without invented progress")]
        public void PauseCheckpointChangesThenExpiresWithoutInventedProgress()
        {
            var f = new SteamCounterFreshness();
            var p = new SteamProgress();
            var now = new DateTime(2026, 10, 6);
            f.Observe(M(1030, 100, 0), now);
            var m = M(1030, 100, 77);
            Eq((int?)77, p.Evaluate(m, false, f.Observe(m, now.AddSeconds(2)), null).Display.DownloadPercent);
            Eq((int?)77, p.Evaluate(m, false, f.Observe(m, now.AddSeconds(6)), null).Display.DownloadPercent);
            Eq((ulong?)null, p.Evaluate(m, false, f.Observe(m, now.AddSeconds(8)), null).Display.Downloaded);
            Eq((int?)80, p.Evaluate(M(1030, 100, 80), false, f.Observe(M(1030, 100, 80), now.AddSeconds(9)), null).Display.DownloadPercent);
        }

        [Test]
        [Description("initial nonzero checkpoint cannot be mistaken for live progress")]
        public void InitialNonzeroCheckpointCannotBeMistakenForLiveProgress()
        {
            Eq(false, new SteamCounterFreshness().Observe(M(1030, 100, 77), DateTime.UtcNow));
        }

        [Test]
        [Description("freshness reset doesn't reuse previous update")]
        public void FreshnessResetDoesnTReusePreviousUpdate()
        {
            var f = new SteamCounterFreshness();
            var now = DateTime.UtcNow;
            f.Observe(M(1030), now);
            f.Observe(M(1030, 100, 50), now);
            f.Reset();
            Eq(false, f.Observe(M(1030, 100, 50), now));
        }

        [Test]
        [Description("shared-only flag is not app running")]
        public void SharedOnlyFlagIsNotAppRunning()
        {
            Eq(false, M(64).IsRunning);
        }

        [Test]
        [Description("app-running flag clears observer")]
        public void AppRunningFlagClearsObserver()
        {
            Eq(UpdatePhase.Running, Eval(new SteamProgress(), M(8196)).Phase);
        }

        [Test]
        [Description("Steam pause is not a disk/error failure")]
        public void SteamPauseIsNotADiskErrorFailure()
        {
            var l = new SteamContentLog("missing", 550);
            l.Apply("AppID 550 update canceled : Disabled (Suspended)");
            Eq((string)null, l.Error);
            Eq(true, l.Paused);
            l.Apply("AppID 550 App update changed : Running Update,Downloading,");
            Eq(false, l.Paused);
            Eq(true, l.Active);
        }

        [Test]
        [Description("download at zero")]
        public void DownloadAtZero()
        {
            Eq((int?)0, Eval(new SteamProgress(), M(1286)).Display.DownloadPercent);
        }

        [Test]
        [Description("download at fifty")]
        public void DownloadAtFifty()
        {
            Eq((int?)50, Eval(new SteamProgress(), M(1286, 100, 50)).Display.DownloadPercent);
        }

        [Test]
        [Description("download 100 does not signal installation completion")]
        public void Download100DoesNotSignalInstallationCompletion()
        {
            var o = Eval(new SteamProgress(), M(1286, 100, 100));
            Eq((int?)100, o.Display.DownloadPercent);
            Eq(UpdatePhase.Updating, o.Phase);
            Eq((int?)null, o.Percent);
        }

        [Test]
        [Description("fallback does not blend download with disk work")]
        public void FallbackDoesNotBlendDownloadWithDiskWork()
        {
            var o = Eval(new SteamProgress(), M(1286, 100, 50, 100, 0));
            Eq((int?)50, o.Display.DownloadPercent);
            Eq((int?)null, o.Percent);
        }

        [Test]
        [Description("patching continues after download")]
        public void PatchingContinuesAfterDownload()
        {
            var o = Eval(new SteamProgress(), M(2098438, 100, 100, 100, 50));
            Eq("Updating files", o.Display.Stage);
            Eq((int?)100, o.Display.DownloadPercent);
            Eq((int?)null, o.Percent);
        }

        [Test]
        [Description("staging complete still verifying")]
        public void StagingCompleteStillVerifying()
        {
            Eq((int?)null, Eval(new SteamProgress(), M(131332, 100, 100, 100, 100)).Percent);
        }

        [Test]
        [Description("commit is updating and cannot complete")]
        public void CommitIsUpdatingAndCannotComplete()
        {
            Eq(UpdatePhase.Updating, Eval(new SteamProgress(), M(4194564, 100, 100, 100, 100)).Phase);
        }

        [Test]
        [Description("staging only does not invent download denominator")]
        public void StagingOnlyDoesNotInventDownloadDenominator()
        {
            Eq("", Eval(new SteamProgress(), M(2098438, 0, 0, 200, 100)).Display.DownloadText);
        }

        [Test]
        [Description("completion confirmed twice then launching")]
        public void CompletionConfirmedTwiceThenLaunching()
        {
            var p = new SteamProgress();
            Eval(p, M(1286));
            Eq(UpdatePhase.Updating, Eval(p, M(4, 100, 100)).Phase);
            Eq(UpdatePhase.Ready, Eval(p, M(4, 100, 100)).Phase);
            Eq(UpdatePhase.Launching, Eval(p, M(4, 100, 100)).Phase);
        }

        [Test]
        [Description("active log vetoes manifest completion")]
        public void ActiveLogVetoesManifestCompletion()
        {
            Eq(UpdatePhase.Updating, new SteamProgress().Evaluate(M(4, 100, 100), true, false, null).Phase);
        }

        [Test]
        [Description("required-only flags don't reuse progress")]
        public void RequiredOnlyFlagsDonTReuseProgress()
        {
            Eq((int?)null, Eval(new SteamProgress(), M(6, 100, 50)).Percent);
        }

        [Test]
        [Description("encrypted installed is not updating")]
        public void EncryptedInstalledIsNotUpdating()
        {
            Eq(UpdatePhase.Launching, Eval(new SteamProgress(), M(12)).Phase);
        }

        [Test]
        [Description("missing byte fields yields unknown")]
        public void MissingByteFieldsYieldsUnknown()
        {
            var m = SteamManifestParser.Parse("\"AppState\" { \"appid\" \"550\" \"StateFlags\" \"1286\" }", 550);
            Eq(UpdatePhase.Updating, Eval(new SteamProgress(), m).Phase);
            Eq((ulong?)null, Eval(new SteamProgress(), m).Display.Downloaded);
        }

        [Test]
        [Description("missing flags cannot infer update from counters")]
        public void MissingFlagsCannotInferUpdateFromCounters()
        {
            Eq(UpdatePhase.Launching, Eval(new SteamProgress(), SteamManifestParser.Parse("AppState { appid 550 BytesToDownload 100 BytesDownloaded 50 }", 550)).Phase);
        }

        [Test]
        [Description("unknown state bits suppress percentage")]
        public void UnknownStateBitsSuppressPercentage()
        {
            Eq((int?)null, Eval(new SteamProgress(), M(1286 | 16777216)).Percent);
        }

        [Test]
        [Description("revised download denominator reflects Steam current measurement")]
        public void RevisedDownloadDenominatorReflectsSteamCurrentMeasurement()
        {
            var p = new SteamProgress();
            Eq((int?)50, Eval(p, M(1286, 100, 50)).Display.DownloadPercent);
            Eq((int?)30, Eval(p, M(1286, 200, 60)).Display.DownloadPercent);
        }

        [Test]
        [Description("real counter restart can go backwards")]
        public void RealCounterRestartCanGoBackwards()
        {
            var p = new SteamProgress();
            Eval(p, M(1286, 100, 70));
            Eq((int?)5, Eval(p, M(1286, 100, 5)).Display.DownloadPercent);
        }

        [Test]
        [Description("stage completion can be 100 without marking game ready")]
        public void StageCompletionCanBe100WithoutMarkingGameReady()
        {
            var s = SteamLiveSample.Parse(LiveFrame(550, 1294, 100, 100, 100, 100), 550);
            Eq((int?)100, LiveEval(new SteamProgress(), s).Percent);
            Eq(UpdatePhase.Updating, LiveEval(new SteamProgress(), s).Phase);
        }
    }
}
