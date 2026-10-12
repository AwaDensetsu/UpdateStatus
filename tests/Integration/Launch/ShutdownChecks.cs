using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteUpdateStatus.Core;

internal static partial class Program
{
    private sealed class ExitSession : IUpdateCloseSession, IUpdateExitSession
    {
        public bool? Paused = false;
        public bool Accept = true, Confirm = true, Throw, Disposed, Prepared, Closed;
        public UpdatePhase Phase = UpdatePhase.Updating;
        public readonly List<string> Actions = new List<string>();
        public ManualResetEvent CommandEntered, CommandRelease, PollEntered, PollRelease;
        public UpdateObservation Poll()
        {
            if (PollEntered != null && !Prepared)
            {
                PollEntered.Set();
                PollRelease.WaitOne(2000);
            }

            return new UpdateObservation
            {
                Phase = Disposed ? UpdatePhase.Idle : Phase,
                ControlPaused = Paused,
                CloseConfirmed = Closed && Confirm,
                CanPause = Paused == false,
                CanResume = Paused == true,
                Display = new UpdateDisplay
                {
                    Stage = "Updating files",
                    Paused = Paused == true
                }
            };
        }

        public bool SendCommand(UpdateCommand command)
        {
            lock (Actions)
                Actions.Add(command.ToString());
            if (CommandEntered != null && command == UpdateCommand.Resume)
            {
                CommandEntered.Set();
                CommandRelease.WaitOne(2000);
            }

            if (Throw)
                throw new InvalidOperationException("Synthetic command failure");
            if (Accept && Confirm)
                Paused = command == UpdateCommand.Pause;
            return Accept;
        }

        public bool SendClose()
        {
            lock (Actions)
                Actions.Add("Close");
            if (Throw)
                throw new InvalidOperationException("Synthetic close failure");
            if (Accept && Confirm)
            {
                Closed = true;
                Paused = true;
            }

            return Accept;
        }

        public void PrepareForExit()
        {
            Prepared = true;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private static UpdateMonitor ExitMonitor(ExitSession session)
    {
        var monitor = new UpdateMonitor((s, w) =>
        {
        });
        monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid(), LauncherId = "550" }, session, 500);
        Until(() => monitor.Current.Phase == session.Phase);
        return monitor;
    }

    private static bool ExitResult(UpdateMonitor monitor, ExitDownloadAction action, int milliseconds = 1000)
    {
        var task = monitor.PrepareForExit(action, TimeSpan.FromMilliseconds(milliseconds));
        Eq(true, task.Wait(milliseconds + 500));
        return task.Result;
    }

    private static void ExitCoreTests()
    {
        foreach (var action in (ExitDownloadAction[])Enum.GetValues(typeof(ExitDownloadAction)))
        {
            var selected = action;
            Test("exit action " + action, () =>
            {
                var session = new ExitSession();
                using (var monitor = ExitMonitor(session))
                {
                    Eq(true, ExitResult(monitor, selected));
                    Eq(true, session.Prepared);
                    Eq(selected == ExitDownloadAction.KeepCurrentState ? 0 : 1, session.Actions.Count);
                    if (session.Actions.Count != 0)
                        Eq(selected == ExitDownloadAction.PauseAndKeepInQueue ? "Pause" : "Close", session.Actions[0]);
                    Eq(false, monitor.RequestCommand(monitor.Current.GameId, UpdateCommand.Resume));
                    Eq(true, ExitResult(monitor, selected));
                    Eq(selected == ExitDownloadAction.KeepCurrentState ? 0 : 1, session.Actions.Count);
                }
            });
        }

        Test("keep state preserves an existing pause", () =>
        {
            var session = new ExitSession
            {
                Paused = true
            };
            using (var monitor = ExitMonitor(session))
            {
                Eq(true, ExitResult(monitor, ExitDownloadAction.KeepCurrentState));
                Eq((bool?)true, session.Paused);
                Eq(0, session.Actions.Count);
            }
        });
        Test("exit pause of already paused update sends no duplicate command", () =>
        {
            var session = new ExitSession
            {
                Paused = true
            };
            using (var monitor = ExitMonitor(session))
            {
                Eq(true, ExitResult(monitor, ExitDownloadAction.PauseAndKeepInQueue));
                Eq(0, session.Actions.Count);
            }
        });
        Test("exit remove also removes an already paused selection", () =>
        {
            var session = new ExitSession
            {
                Paused = true
            };
            using (var monitor = ExitMonitor(session))
            {
                Eq(true, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
                Eq(true, session.Closed);
                Eq(1, session.Actions.Count);
            }
        });
        foreach (var phase in new[]
        {
            UpdatePhase.Ready,
            UpdatePhase.Running,
            UpdatePhase.Idle
        }

        )
        {
            var selected = phase;
            Test("exit does not command a finished or running game " + phase, () =>
            {
                var session = new ExitSession();
                using (var monitor = ExitMonitor(session))
                {
                    session.Phase = selected;
                    Eq(true, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
                    Eq(0, session.Actions.Count);
                }
            });
        }

        Test("exit without an active selection sends no command", () =>
        {
            using (var monitor = new UpdateMonitor((s, w) =>
            {
            }))
                Eq(true, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
        });
        Test("exit with controls disabled sends no command", () =>
        {
            var session = new ExitSession();
            using (var monitor = ExitMonitor(session))
            {
                monitor.ControlsEnabled = false;
                Eq(true, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
                Eq(0, session.Actions.Count);
            }
        });
        Test("unknown global state declines both exit setters", () =>
        {
            foreach (var action in new[]
            {
                ExitDownloadAction.PauseAndKeepInQueue,
                ExitDownloadAction.PauseAndRemoveFromQueue
            }

            )
            {
                var session = new ExitSession
                {
                    Paused = null
                };
                using (var monitor = ExitMonitor(session))
                {
                    Eq(false, ExitResult(monitor, action));
                    Eq(0, session.Actions.Count);
                }
            }
        });
        Test("exit refusal and exception never retry", () =>
        {
            foreach (bool throwing in new[]
            {
                false,
                true
            }

            )
            {
                var session = new ExitSession
                {
                    Accept = false,
                    Throw = throwing
                };
                using (var monitor = ExitMonitor(session))
                {
                    Eq(false, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
                    Eq(1, session.Actions.Count);
                }
            }
        });
        Test("unconfirmed exit is bounded and sends only once", () =>
        {
            var session = new ExitSession
            {
                Confirm = false
            };
            using (var monitor = ExitMonitor(session))
            {
                Eq(false, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue, 180));
                Eq(1, session.Actions.Count);
            }
        });
        Test("invalid exit option declines commands", () =>
        {
            var session = new ExitSession();
            using (var monitor = ExitMonitor(session))
            {
                Eq(false, ExitResult(monitor, (ExitDownloadAction)99));
                Eq(0, session.Actions.Count);
            }
        });
        Test("missing removal capability cannot fall back to another command", () =>
        {
            var session = new GlobalSession();
            using (var monitor = new UpdateMonitor((s, w) =>
            {
            }))
            {
                monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, session, 500);
                Until(() => monitor.Current.CanPause);
                Eq(false, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
                Eq(0, session.Calls);
            }
        });
        Test("exit waits for committed Resume before final pause", ExitResumeOrdering);
        Test("exit drains a running poll before sending removal", ExitPollOrdering);
        Test("prepared selected session never starts or selects an update", () =>
        {
            using (var fixture = new SelectionFixture(550))
            {
                fixture.Session.PrepareForExit();
                Eq(UpdatePhase.Updating, fixture.Session.Poll().Phase);
                Eq(0, fixture.Prepares);
                Eq(0, fixture.Selections);
                Eq(0, fixture.Commands);
            }
        });
        Test("wrong app cannot authorize an exit queue command", () =>
        {
            using (var fixture = new SelectionFixture(550))
            using (var monitor = new UpdateMonitor((s, w) =>
            {
            }))
            {
                var id = Guid.NewGuid();
                monitor.Start(new LaunchIdentity { GameId = id }, fixture.Session, 500);
                Until(() => monitor.Current.CanPause);
                fixture.WrongApp = true;
                Eq(false, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
                Eq(0, fixture.Commands);
            }
        });
        Test("expired and future receipts cannot authorize exit commands", () =>
        {
            foreach (int seconds in new[]
            {
                -10,
                10
            }

            )
                using (var fixture = new SelectionFixture(550))
                using (var monitor = new UpdateMonitor((s, w) =>
                {
                }))
                {
                    monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, fixture.Session, 500);
                    Until(() => monitor.Current.CanPause);
                    fixture.Freeze = true;
                    fixture.Last.ReceivedAtUtc = DateTime.UtcNow.AddSeconds(seconds);
                    Eq(false, ExitResult(monitor, ExitDownloadAction.PauseAndRemoveFromQueue));
                    Eq(0, fixture.Commands);
                }
        });
    }

    private static void ExitResumeOrdering()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            var session = new ExitSession
            {
                Paused = true,
                CommandEntered = entered,
                CommandRelease = release
            };
            using (var monitor = ExitMonitor(session))
            {
                Eq(true, monitor.RequestCommand(monitor.Current.GameId, UpdateCommand.Resume));
                Eq(true, entered.WaitOne(1000));
                var exit = monitor.PrepareForExit(ExitDownloadAction.PauseAndKeepInQueue, TimeSpan.FromSeconds(2));
                Eq(false, exit.IsCompleted);
                release.Set();
                Eq(true, exit.Wait(2500));
                Eq(true, exit.Result);
                Eq("Resume,Pause", string.Join(",", session.Actions));
                Eq((bool?)true, session.Paused);
            }
        }
    }

    private static void ExitPollOrdering()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            var session = new ExitSession
            {
                PollEntered = entered,
                PollRelease = release
            };
            using (var monitor = new UpdateMonitor((s, w) =>
            {
            }))
            {
                monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, session, 500);
                Eq(true, entered.WaitOne(1000));
                var exit = monitor.PrepareForExit(ExitDownloadAction.PauseAndRemoveFromQueue, TimeSpan.FromSeconds(2));
                Eq(0, session.Actions.Count);
                release.Set();
                Eq(true, exit.Wait(2500));
                Eq(true, exit.Result);
                Eq("Close", session.Actions.Single());
            }
        }
    }

    private static void ExitPluginTests()
    {
        Test("exit setting migrates, validates, cancels and saves across modes", ExitSettings);
        foreach (ApplicationMode mode in new[]
        {
            ApplicationMode.Desktop,
            ApplicationMode.Fullscreen
        }

        )
            foreach (var action in (ExitDownloadAction[])Enum.GetValues(typeof(ExitDownloadAction)))
            {
                var selectedMode = mode;
                var selectedAction = action;
                Test("normal " + mode + " shutdown " + action, () => ExitPlugin(selectedMode, selectedAction));
            }

        Test("ordinary Dispose never applies the configured exit download action", () =>
        {
            var calls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher
            };
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                var session = new ExitSession();
                plugin.Settings.ExitAction = ExitDownloadAction.PauseAndRemoveFromQueue;
                plugin.Monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, session, 500);
                Until(() => plugin.Monitor.Current.CanPause);
                plugin.Dispose();
                plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
                Eq(0, session.Actions.Count);
                Eq(true, session.Disposed);
            }
        });
        Test("shutdown disarms an already queued SDK Resume", ExitQueuedResume);
    }

    private static void ExitPlugin(ApplicationMode mode, ExitDownloadAction action)
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            Mode = mode
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var fixture = new SelectionFixture(550))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            plugin.Settings.Enabled = true;
            plugin.Settings.ExitAction = action;
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, fixture } });
            var game = SelectedGame(550);
            var args = Event<OnGameStartingEventArgs>(game);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    plugin.OnGameStarting(args);
                }
                finally
                {
                    returned.Set();
                }
            });
            try
            {
                PumpUntil(() => plugin.Monitor.Current.CanPause);
                plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
                Eq(true, returned.WaitOne(2000));
                Eq(true, args.CancelStartup);
                Eq(0, calls.Starts);
                Eq(action == ExitDownloadAction.KeepCurrentState ? 0 : 1, fixture.Commands);
                Eq(action == ExitDownloadAction.PauseAndRemoveFromQueue ? 1 : 0, fixture.Removals);
                Eq(action == ExitDownloadAction.KeepCurrentState, fixture.Enabled);
                Eq(1, fixture.Disposals);
                Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
                plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(game));
                plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
                var late = Event<OnGameStartingEventArgs>(game);
                plugin.OnGameStarting(late);
                Eq(true, late.CancelStartup);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                Eq(0, calls.Starts);
                Eq(action == ExitDownloadAction.KeepCurrentState ? 0 : 1, fixture.Commands);
            }
            finally
            {
                plugin.Dispose();
                returned.WaitOne(1500);
            }
        }
    }

    private static void ExitSettings()
    {
        var old = Playnite.SDK.Data.Serialization.FromJson<PlayniteUpdateStatus.Settings>("{\"Enabled\":true}");
        Eq(ExitDownloadAction.KeepCurrentState, old.ExitAction);
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            DataPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "artifacts", "validation", "exit-settings"))
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        {
            plugin.Settings.Enabled = false;
            plugin.Settings.ExitAction = ExitDownloadAction.KeepCurrentState;
            var view = BindSettingsView(new PlayniteUpdateStatus.UI.SettingsView((PlayniteUpdateStatus.SettingsViewModel)plugin.GetSettings(false), () => null));
            var panel = (StackPanel)view.FindName("SteamSettingsPanel");
            var input = panel.Children.OfType<ComboBox>().Single(c => c.Name == "ExitActionInput");
            Eq("Keep current state,Pause and keep in queue,Pause and remove from queue", string.Join(",", input.Items.OfType<ComboBoxItem>().Select(c => (string)c.Content)));
            var window = new TestWindow
            {
                Content = view,
                Width = 600,
                Height = 700
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                plugin.GetSettings(false).BeginEdit();
                input.SelectedIndex = 2;
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() =>
                {
                }));
                Eq(ExitDownloadAction.PauseAndRemoveFromQueue, plugin.Settings.ExitAction);
                plugin.GetSettings(false).CancelEdit();
                Eq(ExitDownloadAction.KeepCurrentState, plugin.Settings.ExitAction);
            }
            finally
            {
                window.Close();
            }

            List<string> errors;
            plugin.Settings.ExitAction = (ExitDownloadAction)99;
            Eq(false, plugin.GetSettings(false).VerifySettings(out errors));
            plugin.Settings.ExitAction = ExitDownloadAction.PauseAndKeepInQueue;
            plugin.GetSettings(false).EndEdit();
        }

        calls.Mode = ApplicationMode.Fullscreen;
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            Eq(ExitDownloadAction.PauseAndKeepInQueue, plugin.Settings.ExitAction);
    }

    private static void ExitQueuedResume()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var fixture = new SelectionFixture(550))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            plugin.Settings.Enabled = true;
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, fixture } });
            plugin.Settings.ExitAction = ExitDownloadAction.PauseAndRemoveFromQueue;
            var game = SelectedGame(550);
            calls.DatabaseGame = game;
            var args = Event<OnGameStartingEventArgs>(game);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    plugin.OnGameStarting(args);
                }
                finally
                {
                    returned.Set();
                }
            });
            try
            {
                PumpUntil(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                Eq(true, returned.WaitOne(1500));
                Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                AcknowledgeSdkCancellation(plugin, args, game);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                Until(() => plugin.Monitor.Current.CanPause && !plugin.Monitor.Current.CommandPending);
                // Do not pump the queued UI restart until shutdown has invalidated it.
                plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                Eq(0, calls.Starts);
                Eq(1, fixture.Removals);
                Eq(3, fixture.Commands);
                Eq(false, fixture.Enabled);
            }
            finally
            {
                plugin.Dispose();
                returned.WaitOne(1500);
            }
        }
    }
}
