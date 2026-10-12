using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Threading;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

internal static partial class Program
{
    private sealed class MissingCounterControlSession : IUpdateControlSession
    {
        public volatile bool Paused, Counters = true, GlobalsKnown = true;
        public int Calls;
        public UpdateObservation Poll()
        {
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating,
                CanPause = !Paused,
                CanResume = Paused,
                ControlPaused = GlobalsKnown ? (bool?)Paused : null,
                Display = new UpdateDisplay
                {
                    Stage = Paused ? "Paused" : "Starting download",
                    Paused = Paused,
                    WaitingForData = !Counters
                }
            };
        }

        public bool SendCommand(UpdateCommand command)
        {
            Interlocked.Increment(ref Calls);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static void PauseConfirmationTests()
    {
        Test("Paused metadata replaces the previous stage without inventing counters", () =>
        {
            var sample = new SteamLiveSample
            {
                AppId = 381210,
                InstallState = 14,
                Result = 4,
                DownloadsEnabled = false,
                HasCounters = false
            };
            var display = SteamLivePresentation.Build(sample, "Starting download");
            Eq(true, display.Paused);
            Eq("Paused…", display.StageText);
            Eq(true, display.WaitingForData);
            Eq((ulong?)null, display.DownloadTotal);
            Eq((int?)null, display.WorkPercent);
            sample.Result = 0;
            sample.DownloadsEnabled = true;
            display = SteamLivePresentation.Build(sample, "Paused");
            Eq(false, display.Paused);
            Eq("Queued", display.Stage);
        });
        Test("Pause and Resume confirm from global state when Steam drops counters", () =>
        {
            using (var monitor = new UpdateMonitor((s, w) =>
            {
            }))
            {
                var id = Guid.NewGuid();
                var session = new MissingCounterControlSession();
                int confirmations = 0;
                monitor.CommandConfirmed += (game, token, command) => Interlocked.Increment(ref confirmations);
                monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
                Until(() => monitor.Current.CanPause);
                Eq(true, monitor.RequestCommand(id, UpdateCommand.Pause));
                Until(() => monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
                session.Counters = false;
                session.Paused = true;
                Until(() => confirmations == 1 && !monitor.Current.CommandPending);
                Eq((string)null, monitor.Current.CommandNotice);
                Eq(true, monitor.Current.CanResume);
                Eq((ulong?)null, monitor.Current.Display.DownloadTotal);
                Eq((int?)null, monitor.Current.Percent);
                Eq(true, monitor.RequestCommand(id, UpdateCommand.Resume));
                Until(() => monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
                session.Paused = false;
                Until(() => confirmations == 2 && !monitor.Current.CommandPending);
                Eq((string)null, monitor.Current.CommandNotice);
                Eq(true, monitor.Current.CanPause);
            }
        });
        Test("Missing global state and unknown progress cannot confirm Pause", () =>
        {
            using (var monitor = new UpdateMonitor((s, w) =>
            {
            }))
            {
                var id = Guid.NewGuid();
                var session = new MissingCounterControlSession();
                int confirmations = 0;
                monitor.CommandConfirmed += (game, token, command) => Interlocked.Increment(ref confirmations);
                monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
                Until(() => monitor.Current.CanPause);
                Eq(true, monitor.RequestCommand(id, UpdateCommand.Pause));
                Until(() => monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
                session.Counters = false;
                session.GlobalsKnown = false;
                session.Paused = true;
                Until(() => monitor.Current.ControlPaused == null && monitor.Current.Display.WaitingForData);
                Eq(true, monitor.Current.CommandPending);
                Eq(0, confirmations);
                session.GlobalsKnown = true;
                Until(() => confirmations == 1 && !monitor.Current.CommandPending);
            }
        });
    }
}
