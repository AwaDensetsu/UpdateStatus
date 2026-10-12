using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting.Messaging;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;
using static UpdateStatus.Tests.Support.SteamSamples;

internal static partial class Program
{
    private sealed class RecoveryFixture : IDisposable
    {
        public readonly string PathName = Path.Combine(Path.GetTempPath(), "playnite-launch-" + Guid.NewGuid() + ".txt");
        public readonly SteamLaunchJournal Journal;
        public readonly SteamLaunchRecovery Recovery;
        public readonly DateTime Now = DateTime.UtcNow;
        public int Starts;
        public bool Running, Fail;
        public string Uri;
        public RecoveryFixture()
        {
            File.WriteAllText(PathName, "ExecuteSteamURL: \"steam://rungameid/550\"\nGameAction [AppID 550, ActionID 1] : LaunchApp changed task to DownloadingDepots with \"\"\n");
            Journal = new SteamLaunchJournal(PathName, 550);
            Recovery = new SteamLaunchRecovery(Journal, () => Running, uri =>
            {
                Starts++;
                Uri = uri;
                if (Fail)
                    throw new IOException("fixture failure");
            }, (s, d) =>
            {
            });
        }

        public void Append(string line)
        {
            File.AppendAllText(PathName, line + "\n");
        }

        public void Launch(string uri = "steam://rungameid/550")
        {
            Append("ExecuteSteamURL: \"" + uri + "\"");
            Task("DownloadingDepots");
        }

        public void Task(string task, int action = 2, int app = 550)
        {
            Append("GameAction [AppID " + app + ", ActionID " + action + "] : LaunchApp changed task to " + task + " with \"\"");
        }

        public void State(UpdatePhase phase, int seconds, bool pending = false, bool dismissed = false)
        {
            Recovery.Observe(new UpdateState { Phase = phase, CommandPending = pending, Dismissed = dismissed }, Now.AddSeconds(seconds));
        }

        public void Ready()
        {
            Launch();
            State(UpdatePhase.Updating, 0);
            Recovery.CommandConfirmed(UpdateCommand.Resume);
            State(UpdatePhase.Ready, 1);
            State(UpdatePhase.Launching, 10);
        }

        public void Dispose()
        {
            File.Delete(PathName);
        }
    }

    private static void RecoveryTests()
    {
        Test("completion before a slow Resume acknowledgement retains the request", () =>
        {
            using (var release = new ManualResetEvent(false))
            using (var monitor = new UpdateMonitor((s, d) =>
            {
            }))
            {
                var session = new ResumeSession
                {
                    Paused = true,
                    CompleteOnResume = true,
                    AckRelease = release
                };
                var id = Guid.NewGuid();
                int confirmations = 0;
                monitor.CommandConfirmed += (game, token, command) => Interlocked.Increment(ref confirmations);
                monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
                Until(() => monitor.Current.CanResume);
                try
                {
                    Eq(true, monitor.RequestCommand(id, UpdateCommand.Resume));
                    Until(() => monitor.Current.Phase == UpdatePhase.Ready);
                    Eq(true, monitor.Current.CommandPending);
                    Eq(0, confirmations);
                    release.Set();
                    Until(() => confirmations == 1 && !monitor.Current.CommandPending);
                    Eq(1, confirmations);
                }
                finally
                {
                    release.Set();
                }
            }
        });
        Test("Resume confirmation survives update completion before next poll", () =>
        {
            using (var monitor = new UpdateMonitor((s, d) =>
            {
            }))
            {
                var session = new ResumeSession
                {
                    Paused = true,
                    CompleteOnResume = true
                };
                var id = Guid.NewGuid();
                int confirmations = 0;
                monitor.CommandConfirmed += (game, token, command) =>
                {
                    Eq(id, game);
                    Eq(UpdateCommand.Resume, command);
                    Interlocked.Increment(ref confirmations);
                };
                monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
                Until(() => monitor.Current.CanResume);
                Eq(true, monitor.RequestCommand(id, UpdateCommand.Resume));
                Until(() => monitor.Current.Phase == UpdatePhase.Ready && !monitor.Current.CommandPending);
                Until(() => confirmations == 1);
                Thread.Sleep(600);
                Eq(1, confirmations);
            }
        });
        Test("completed resumed update immediately restores exact original Steam request only once", () =>
        {
            foreach (string uri in new[]
            {
                "steam://rungameid/550",
                "steam://launch/550/Dialog"
            }

            )
                using (var f = new RecoveryFixture())
                {
                    f.Launch(uri);
                    f.State(UpdatePhase.Updating, 0);
                    f.Recovery.CommandConfirmed(UpdateCommand.Resume);
                    f.Recovery.Poll(f.Now);
                    Eq(0, f.Starts);
                    f.State(UpdatePhase.Ready, 1);
                    f.Recovery.Poll(f.Now.AddSeconds(1));
                    Eq(1, f.Starts);
                    Eq(uri, f.Uri);
                    f.State(UpdatePhase.Launching, 10);
                    f.Recovery.Poll(f.Now.AddSeconds(10));
                    Eq(1, f.Starts);
                    Eq(uri, f.Uri);
                    f.Recovery.CommandConfirmed(UpdateCommand.Resume);
                    f.Recovery.Poll(f.Now.AddSeconds(11));
                    Eq(1, f.Starts);
                }
        });
        Test("initial Play and unconfirmed Resume cannot cause another launch", () =>
        {
            using (var f = new RecoveryFixture())
            {
                f.Launch();
                f.State(UpdatePhase.Ready, 1);
                f.State(UpdatePhase.Launching, 10);
                f.Recovery.Poll(f.Now.AddSeconds(10));
                Eq(0, f.Starts);
                f.Recovery.CommandRequested(UpdateCommand.Resume);
                f.Recovery.Poll(f.Now.AddSeconds(10));
                Eq(0, f.Starts);
            }
        });
        Test("native cloud/dialog/process progression suppresses recovery", () =>
        {
            foreach (string task in new[]
            {
                "SynchronizingCloud",
                "ShowInterstitials",
                "CreatingProcess",
                "Completed",
                "Cancelled",
                "UnknownFutureTask"
            }

            )
                using (var f = new RecoveryFixture())
                {
                    f.Ready();
                    f.Task(task);
                    f.Recovery.Poll(f.Now.AddSeconds(10));
                    Eq(0, f.Starts);
                    f.Task("DownloadingDepots");
                    f.Recovery.Poll(f.Now.AddSeconds(11));
                    Eq(0, f.Starts);
                }
        });
        Test("pause, close, running and stale observations prevent deferred launch", () =>
        {
            for (int i = 0; i < 6; i++)
                using (var f = new RecoveryFixture())
                {
                    f.Ready();
                    if (i == 0)
                        f.Recovery.CommandRequested(UpdateCommand.Pause);
                    if (i == 1)
                        f.Recovery.Cancel();
                    if (i == 2)
                        f.Running = true;
                    if (i == 3)
                        f.State(UpdatePhase.Launching, 10, true);
                    if (i == 4)
                        f.State(UpdatePhase.Launching, 10, false, true);
                    f.Recovery.Poll(f.Now.AddSeconds(i == 5 ? 20 : 10));
                    Eq(0, f.Starts);
                }
        });
        Test("interrupted update and 100 percent work do not launch prematurely", () =>
        {
            using (var f = new RecoveryFixture())
            {
                f.Ready();
                f.State(UpdatePhase.Error, 10);
                f.Recovery.Poll(f.Now.AddSeconds(10));
                Eq(0, f.Starts);
                f.State(UpdatePhase.Updating, 11);
                f.Recovery.Poll(f.Now.AddSeconds(11));
                Eq(0, f.Starts);
                f.State(UpdatePhase.Launching, 12);
                f.Recovery.Poll(f.Now.AddSeconds(12));
                Eq(0, f.Starts);
            }
        });
        Test("superseding Steam play for same or different game cannot be replayed", () =>
        {
            for (int i = 0; i < 3; i++)
                using (var f = new RecoveryFixture())
                {
                    f.Ready();
                    if (i == 0)
                        f.Task("DownloadingDepots", 3);
                    else
                        f.Append("ExecuteSteamURL: \"steam://rungameid/" + (i == 1 ? "551" : "550") + "\"");
                    f.Recovery.Poll(f.Now.AddSeconds(10));
                    Eq(0, f.Starts);
                }
        });
        Test("historical launch lines, unsafe/custom URI and absent action cannot be reused", () =>
        {
            foreach (string uri in new[]
            {
                null,
                "steam://launch/550/Other",
                "steam://rungameid/551",
                "steam://rungameid/550/extra",
                "steam://rungameid/550\" -bad"
            }

            )
                using (var f = new RecoveryFixture())
                {
                    if (uri != null)
                        f.Launch(uri);
                    f.Recovery.CommandConfirmed(UpdateCommand.Resume);
                    f.State(UpdatePhase.Ready, 1);
                    f.State(UpdatePhase.Launching, 10);
                    f.Recovery.Poll(f.Now.AddSeconds(10));
                    Eq(0, f.Starts);
                }

            using (var f = new RecoveryFixture())
            {
                f.Append("ExecuteSteamURL: \"steam://rungameid/550\"");
                f.Recovery.CommandConfirmed(UpdateCommand.Resume);
                f.State(UpdatePhase.Ready, 1);
                f.State(UpdatePhase.Launching, 10);
                f.Recovery.Poll(f.Now.AddSeconds(10));
                Eq(0, f.Starts);
            }
        });
        Test("truncated or missing Steam journal never authorizes recovery", () =>
        {
            for (int i = 0; i < 2; i++)
                using (var f = new RecoveryFixture())
                {
                    f.Ready();
                    f.Journal.Poll();
                    if (i == 0)
                        File.WriteAllText(f.PathName, "");
                    else
                        File.Delete(f.PathName);
                    f.Recovery.Poll(f.Now.AddSeconds(10));
                    Eq(0, f.Starts);
                }
        });
        Test("failed recovery is never automatically retried", () =>
        {
            using (var f = new RecoveryFixture())
            {
                f.Ready();
                f.Fail = true;
                f.Recovery.Poll(f.Now.AddSeconds(10));
                Eq(1, f.Starts);
                f.Fail = false;
                f.Recovery.Poll(f.Now.AddSeconds(11));
                Eq(1, f.Starts);
            }
        });
    }

    private sealed class ResumeSession : IUpdateControlSession
    {
        public volatile bool Paused;
        public bool CompleteOnResume;
        public bool FreshReceipt;
        public bool WaitingForData;
        public ManualResetEvent AckRelease;
        public volatile UpdatePhase Phase = UpdatePhase.Updating;
        public UpdateObservation Poll()
        {
            return new UpdateObservation
            {
                Phase = Phase,
                ReceivedAtUtc = FreshReceipt ? DateTime.UtcNow : (DateTime?)null,
                ControlPaused = Paused,
                CanPause = Phase == UpdatePhase.Updating && !Paused,
                CanResume = Phase == UpdatePhase.Updating && Paused,
                Display = new UpdateDisplay
                {
                    Paused = Paused,
                    WaitingForData = WaitingForData
                }
            };
        }

        public bool SendCommand(UpdateCommand command)
        {
            Paused = command == UpdateCommand.Pause;
            if (command == UpdateCommand.Resume && CompleteOnResume)
                Phase = UpdatePhase.Ready;
            if (AckRelease != null)
                AckRelease.WaitOne(4000);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static void ResumeLaunchPlugin()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        for (int close = 0; close < 2; close++)
            using (var f = new RecoveryFixture())
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                var session = new ResumeSession();
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    PluginId = SteamGameIdentity.LibraryId,
                    GameId = "550",
                    Name = "Resume fixture"
                };
                calls.DatabaseGame = game;
                var registry = new LauncherRegistry();
                registry.Register(new LauncherRegistration("steam", new NormalAdapter(session), SteamGameIdentity.Resolve, null, null, UpdateCapabilities.Phases | UpdateCapabilities.PauseResume));
                SetField(LaunchCoordinatorFor(plugin), "launchers", registry);
                SetField(LaunchCoordinatorFor(plugin), "recoveryFactory", new Func<uint, Func<bool>, Action<string, bool>, SteamLaunchRecovery>((id, running, log) => f.Recovery));
                var args = Event<OnGameStartingEventArgs>(game);
                plugin.OnGameStarting(args);
                Eq(false, args.CancelStartup);
                Eq(0, f.Starts);
                f.Launch();
                Until(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                Until(() => plugin.Monitor.Current.CanPause && !plugin.Monitor.Current.CommandPending);
                if (close == 1)
                {
                    Eq(true, plugin.Monitor.RequestClose(game.Id, plugin.Monitor.Current.SessionId));
                    PumpUntil(() => plugin.Monitor.Current.Phase == UpdatePhase.Idle);
                }

                session.Phase = UpdatePhase.Ready;
                if (close == 0)
                    Until(() => plugin.Monitor.Current.Phase == UpdatePhase.Ready);
                // Verify immediate Ready recovery without an actual Steam command.
                f.Recovery.Observe(new UpdateState { Phase = UpdatePhase.Updating }, f.Now);
                f.Recovery.Observe(new UpdateState { Phase = UpdatePhase.Ready }, f.Now.AddSeconds(1));
                f.Recovery.Poll(f.Now.AddSeconds(1));
                Eq(close == 0 ? 1 : 0, f.Starts);
                Eq(0, calls.Starts);
                Eq(false, args.CancelStartup);
            }
    }
}
