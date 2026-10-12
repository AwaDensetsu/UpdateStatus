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
    private static void SelectedPluginHandoff()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var first = new SelectionFixture(550))
        using (var next = new SelectionFixture(551))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var firstReturned = new ManualResetEvent(false))
        using (var nextReturned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next } });
            next.OnSelection = () =>
            {
                Eq(true, first.Removed);
                Eq(true, first.RemovalConfirmed);
            };
            var oldGame = SelectedGame(550);
            var newGame = SelectedGame(551);
            var oldArgs = Event<OnGameStartingEventArgs>(oldGame);
            var nextArgs = Event<OnGameStartingEventArgs>(newGame);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    plugin.OnGameStarting(oldArgs);
                }
                finally
                {
                    firstReturned.Set();
                }
            });
            try
            {
                Until(() => plugin.Monitor.Current.GameId == oldGame.Id && plugin.Monitor.Current.CanPause);
                Eq(false, firstReturned.WaitOne(0));
                Eq(1, first.Selections);
                Eq(0, calls.Starts);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(nextArgs);
                    }
                    finally
                    {
                        nextReturned.Set();
                    }
                });
                Until(() => plugin.Monitor.Current.GameId == newGame.Id && plugin.Monitor.Current.CanPause);
                Eq(true, firstReturned.WaitOne(1500));
                Eq(true, oldArgs.CancelStartup);
                Eq(false, nextArgs.CancelStartup);
                Eq(1, first.Removals);
                Eq(true, first.Removed);
                Eq(false, first.Enabled);
                Eq(0, next.Removals);
                plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(oldGame));
                Eq(newGame.Id, plugin.Monitor.Current.GameId);
                first.Complete = true;
                Eq(1, first.Disposals);
                Eq(false, nextReturned.WaitOne(0));
                Eq(1, next.Selections);
                next.Complete = true;
                Eq(true, nextReturned.WaitOne(2500));
                Eq(false, nextArgs.CancelStartup);
                Eq((GameAction)null, nextArgs.SourceAction);
                Eq(0, calls.Starts);
                Eq(1, next.Selections);
                plugin.OnGameStarted(Event<OnGameStartedEventArgs>(newGame));
                Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
            }
            finally
            {
                plugin.Dispose();
                firstReturned.WaitOne(1500);
                nextReturned.WaitOne(1500);
            }
        }
    }

    private static void SelectedHandoffConfirmation()
    {
        foreach (bool accepted in new[]
        {
            true,
            false
        }

        )
        {
            var calls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher
            };
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
            using (var first = new SelectionFixture(550))
            using (var next = new SelectionFixture(551))
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            using (var firstReturned = new ManualResetEvent(false))
            using (var nextReturned = new ManualResetEvent(false))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next } });
                first.RemovalAccept = accepted;
                first.RemovalConfirmed = false;
                var oldGame = SelectedGame(550);
                var newGame = SelectedGame(551);
                var oldArgs = Event<OnGameStartingEventArgs>(oldGame);
                var newArgs = Event<OnGameStartingEventArgs>(newGame);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(oldArgs);
                    }
                    finally
                    {
                        firstReturned.Set();
                    }
                });
                try
                {
                    Until(() => plugin.Monitor.Current.CanPause);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            plugin.OnGameStarting(newArgs);
                        }
                        finally
                        {
                            nextReturned.Set();
                        }
                    });
                    Until(() => first.Removals == 1);
                    Eq(true, firstReturned.WaitOne(1500));
                    Eq(true, oldArgs.CancelStartup);
                    if (accepted)
                    {
                        Eq(false, nextReturned.WaitOne(150));
                        Eq(oldGame.Id, plugin.Monitor.Current.GameId);
                        Eq(0, next.Selections);
                        Eq(false, plugin.Monitor.Current.Dismissed);
                        first.RemovalConfirmed = true;
                        Until(() => plugin.Monitor.Current.GameId == newGame.Id && plugin.Monitor.Current.CanPause);
                        next.Complete = true;
                        Eq(true, nextReturned.WaitOne(2500));
                        Eq(false, newArgs.CancelStartup);
                    }
                    else
                    {
                        Eq(true, nextReturned.WaitOne(1500));
                        Eq(true, newArgs.CancelStartup);
                        Eq(0, next.Selections);
                        Eq(false, first.Removed);
                        Until(() => !plugin.Monitor.Current.CommandPending);
                        Eq(oldGame.Id, plugin.Monitor.Current.GameId);
                        Eq(false, plugin.Monitor.Current.Dismissed);
                        Eq(true, plugin.Monitor.Current.CommandNotice.Contains("not accepted"));
                        first.Complete = true;
                        first.Enabled = true;
                        Eq(true, oldArgs.CancelStartup);
                        plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(newGame));
                        Eq(oldGame.Id, plugin.Monitor.Current.GameId);
                    }

                    Eq(1, first.Removals);
                    Eq(0, calls.Starts);
                    Eq(0, calls.LibraryReturns + calls.FullscreenToggles);
                }
                finally
                {
                    plugin.Dispose();
                    firstReturned.WaitOne(1500);
                    nextReturned.WaitOne(1500);
                }
            }
        }
    }

    private static void SelectedRapidHandoff()
    {
        foreach (bool sameGame in new[]
        {
            false,
            true
        }

        )
        {
            var calls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher
            };
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
            using (var first = new SelectionFixture(550))
            using (var latest = new SelectionFixture(sameGame ? 551u : 552u))
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var firstReturned = new ManualResetEvent(false))
            using (var middleReturned = new ManualResetEvent(false))
            using (var lastReturned = new ManualResetEvent(false))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { latest.AppId, latest } });
                first.RemovalEntered = entered;
                first.RemovalRelease = release;
                var oldGame = SelectedGame(550);
                var middle = SelectedGame(551);
                var last = sameGame ? middle : SelectedGame(552);
                var oldArgs = Event<OnGameStartingEventArgs>(oldGame);
                var middleArgs = Event<OnGameStartingEventArgs>(middle);
                var lastArgs = Event<OnGameStartingEventArgs>(last);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(oldArgs);
                    }
                    finally
                    {
                        firstReturned.Set();
                    }
                });
                try
                {
                    Until(() => plugin.Monitor.Current.CanPause);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            plugin.OnGameStarting(middleArgs);
                        }
                        finally
                        {
                            middleReturned.Set();
                        }
                    });
                    Eq(true, entered.WaitOne(1500));
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            plugin.OnGameStarting(lastArgs);
                        }
                        finally
                        {
                            lastReturned.Set();
                        }
                    });
                    Eq(true, middleReturned.WaitOne(1500));
                    Eq(true, middleArgs.CancelStartup);
                    Eq(0, latest.Selections);
                    Eq(1, first.Removals);
                    release.Set();
                    Until(() => plugin.Monitor.Current.GameId == last.Id && plugin.Monitor.Current.CanPause);
                    plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(middle));
                    Eq(last.Id, plugin.Monitor.Current.GameId);
                    Eq(true, firstReturned.WaitOne(1500));
                    Eq(true, oldArgs.CancelStartup);
                    Eq(1, first.Removals);
                    Eq(0, latest.Removals);
                    Eq(1, latest.Selections);
                    latest.Complete = true;
                    Eq(true, lastReturned.WaitOne(2500));
                    Eq(false, lastArgs.CancelStartup);
                    Eq(0, calls.Starts);
                }
                finally
                {
                    release.Set();
                    plugin.Dispose();
                    firstReturned.WaitOne(1500);
                    middleReturned.WaitOne(1500);
                    lastReturned.WaitOne(1500);
                }
            }
        }
    }

    private static void SelectedUiHandoff()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            Mode = ApplicationMode.Fullscreen
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var first = new SelectionFixture(550))
        using (var next = new SelectionFixture(551))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next } });
            first.RemovalConfirmed = false;
            var oldArgs = Event<OnGameStartingEventArgs>(SelectedGame(550));
            var nextArgs = Event<OnGameStartingEventArgs>(SelectedGame(551));
            Exception error = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    plugin.OnGameStarting(oldArgs);
                }
                finally
                {
                    returned.Set();
                }
            });
            Until(() => plugin.Monitor.Current.CanPause);
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            timer.Tick += (s, e) =>
            {
                try
                {
                    if (first.Removals == 1)
                        first.RemovalConfirmed = true;
                    if (next.Selections == 1)
                    {
                        next.Complete = true;
                        timer.Stop();
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                    plugin.Dispose();
                    timer.Stop();
                }
            };
            timer.Start();
            try
            {
                plugin.OnGameStarting(nextArgs);
                if (error != null)
                    throw error;
                Eq(false, nextArgs.CancelStartup);
                Eq(true, oldArgs.CancelStartup);
                Eq(true, returned.WaitOne(1500));
                Eq(1, first.Removals);
                Eq(0, calls.LibraryReturns + calls.FullscreenToggles);
            }
            finally
            {
                timer.Stop();
                plugin.Dispose();
                returned.WaitOne(1500);
            }
        }
    }

    private static void SelectedHandoffNormalGame()
    {
        foreach (bool steam in new[]
        {
            true,
            false
        }

        )
        {
            var calls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher
            };
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
            using (var first = new SelectionFixture(550))
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            using (var returned = new ManualResetEvent(false))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first } });
                SetField(LaunchCoordinatorFor(plugin), "selectionPreflight", new Func<LaunchIdentity, SteamCatalogSnapshot, bool>((id, catalog) => id.LauncherId == "550"));
                SetField(LaunchCoordinatorFor(plugin), "recoveryFactory", new Func<uint, Func<bool>, Action<string, bool>, SteamLaunchRecovery>((id, running, log) => null));
                var oldArgs = Event<OnGameStartingEventArgs>(SelectedGame(550));
                var nextGame = steam ? SelectedGame(551) : new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Manual game"
                };
                var nextArgs = Event<OnGameStartingEventArgs>(nextGame);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(oldArgs);
                    }
                    finally
                    {
                        returned.Set();
                    }
                });
                try
                {
                    Until(() => plugin.Monitor.Current.CanPause);
                    plugin.OnGameStarting(nextArgs);
                    Eq(false, nextArgs.CancelStartup);
                    Eq(1, first.Removals);
                    Eq(true, first.Removed);
                    Eq(true, oldArgs.CancelStartup);
                    Eq(true, returned.WaitOne(1500));
                    Eq(0, calls.Starts);
                    if (steam)
                        Eq(nextGame.Id, plugin.Monitor.Current.GameId);
                    else
                        Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
                }
                finally
                {
                    plugin.Dispose();
                    returned.WaitOne(1500);
                }
            }
        }
    }

    private static void SelectedPluginClose(ApplicationMode mode)
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            Mode = mode
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var f = new SelectionFixture(550))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, f } });
            var game = SelectedGame(550);
            calls.DatabaseGame = game;
            var args = Event<OnGameStartingEventArgs>(game);
            var model = new LibraryFixture(game);
            var previous = Application.Current.MainWindow;
            calls.FullscreenView = FullscreenView.Details;
            calls.OnFullscreenToggle = model.ToggleSdkDetails;
            var window = new TestWindow
            {
                Width = 600,
                Height = 400,
                DataContext = model,
                Content = new Grid(),
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                Application.Current.MainWindow = window;
                window.Show();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(args);
                        AcknowledgeSdkCancellation(plugin, args, game, model);
                    }
                    finally
                    {
                        returned.Set();
                    }
                });
                PumpUntil(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestClose(game.Id, plugin.Monitor.Current.SessionId));
                PumpUntil(() => returned.WaitOne(0) && !game.IsLaunching && plugin.Monitor.Current.Phase == UpdatePhase.Idle && calls.LibraryReturns + calls.FullscreenToggles == 1);
                Eq(true, args.CancelStartup);
                Eq(false, f.Enabled);
                Eq(1, f.Commands);
                Eq(1, f.Removals);
                Eq(0, calls.Starts);
                Eq(0, model.ConfirmationPrompts);
                Eq(mode == ApplicationMode.Desktop ? 1 : 0, calls.LibraryReturns);
                Eq(0, model.StatusCloses);
                Eq(1, model.SdkCancellations);
                Eq(mode == ApplicationMode.Fullscreen ? 1 : 0, calls.FullscreenToggles);
                f.Complete = true;
                f.Enabled = true;
                Eq(UpdatePhase.Idle, f.Session.Poll().Phase);
                Eq(0, calls.Starts);
            }
            finally
            {
                plugin.Dispose();
                returned.WaitOne(1500);
                window.Close();
                Application.Current.MainWindow = previous;
            }
        }
    }

    private static void SelectedSdkAckOrdering(bool acknowledgeFirst)
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            Mode = acknowledgeFirst ? ApplicationMode.Fullscreen : ApplicationMode.Desktop,
            FullscreenView = FullscreenView.Details
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var f = new SelectionFixture(550))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, f } });
            var game = SelectedGame(550);
            var args = Event<OnGameStartingEventArgs>(game);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    plugin.OnGameStarting(args);
                    if (acknowledgeFirst)
                        AcknowledgeSdkCancellation(plugin, args, game);
                }
                finally
                {
                    returned.Set();
                }
            });
            try
            {
                // Do not pump the dispatcher: exercise the host notification
                // arriving before the queued UI close, rather than faking order.
                Until(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestClose(game.Id, plugin.Monitor.Current.SessionId));
                Eq(true, returned.WaitOne(2500));
                Eq(true, args.CancelStartup);
                Eq(0, calls.LibraryReturns + calls.FullscreenToggles);
                if (!acknowledgeFirst)
                {
                    PumpUntil(() => plugin.Monitor.Current.Phase == UpdatePhase.Idle);
                    Eq(true, game.IsLaunching);
                    Eq(0, calls.LibraryReturns + calls.FullscreenToggles);
                    AcknowledgeSdkCancellation(plugin, args, game);
                }

                PumpUntil(() => plugin.Monitor.Current.Phase == UpdatePhase.Idle && calls.LibraryReturns + calls.FullscreenToggles == 1);
                Eq(false, game.IsLaunching);
                Eq(0, calls.Starts);
                Eq(1, f.Commands);
                Eq(1, f.Removals);
                plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(game));
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                Eq(1, calls.LibraryReturns + calls.FullscreenToggles);
            }
            finally
            {
                plugin.Dispose();
                returned.WaitOne(1500);
            }
        }
    }

    private static void SelectedSdkOldAcknowledgement()
    {
        SelectedSdkOldAcknowledgement(false);
    }

    private static void SelectedSdkOldAcknowledgement(bool sameGame)
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var first = new SelectionFixture(550))
        using (var next = new SelectionFixture(551))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var firstReturned = new ManualResetEvent(false))
        using (var nextReturned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next } });
            var oldGame = SelectedGame(550);
            var nextGame = SelectedGame(551);
            if (sameGame)
                nextGame.Id = oldGame.Id;
            var oldArgs = Event<OnGameStartingEventArgs>(oldGame);
            var nextArgs = Event<OnGameStartingEventArgs>(nextGame);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    plugin.OnGameStarting(oldArgs);
                }
                finally
                {
                    firstReturned.Set();
                }
            });
            try
            {
                PumpUntil(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestClose(oldGame.Id, plugin.Monitor.Current.SessionId));
                PumpUntil(() => firstReturned.WaitOne(0) && plugin.Monitor.Current.Phase == UpdatePhase.Idle);
                Eq(0, calls.LibraryReturns);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(nextArgs);
                    }
                    finally
                    {
                        nextReturned.Set();
                    }
                });
                PumpUntil(() => plugin.Monitor.Current.GameId == nextGame.Id && plugin.Monitor.Current.CanPause);
                AcknowledgeSdkCancellation(plugin, oldArgs, oldGame);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                Eq(nextGame.Id, plugin.Monitor.Current.GameId);
                Eq(false, nextArgs.CancelStartup);
                Eq(false, nextReturned.WaitOne(0));
                Eq(0, calls.LibraryReturns);
                Eq(0, calls.Starts);
                next.Complete = true;
                Eq(true, nextReturned.WaitOne(2500));
                Eq(false, nextArgs.CancelStartup);
            }
            finally
            {
                plugin.Dispose();
                firstReturned.WaitOne(1500);
                nextReturned.WaitOne(1500);
            }
        }
    }

    private static void SelectedSettingsStop()
    {
        foreach (int stop in new[]
        {
            0,
            1
        }

        )
        {
            var calls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher
            };
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
            using (var f = new SelectionFixture(550))
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            using (var returned = new ManualResetEvent(false))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, f } });
                var game = SelectedGame(550);
                var args = Event<OnGameStartingEventArgs>(game);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(args);
                        AcknowledgeSdkCancellation(plugin, args, game);
                    }
                    finally
                    {
                        returned.Set();
                    }
                });
                try
                {
                    PumpUntil(() => plugin.Monitor.Current.CanPause);
                    if (stop == 0)
                    {
                        plugin.Settings.Enabled = false;
                        plugin.ApplySettings();
                    }
                    else
                        plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
                    PumpUntil(() => returned.WaitOne(0));
                    Eq(true, args.CancelStartup);
                    Eq(false, game.IsLaunching);
                    Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
                    f.Complete = true;
                    Eq(UpdatePhase.Idle, f.Session.Poll().Phase);
                    Eq(0, calls.Starts);
                    Eq(0, f.Commands);
                    Eq(0, f.Removals);
                    Eq(1, f.Disposals);
                }
                finally
                {
                    plugin.Dispose();
                    returned.WaitOne(1500);
                }
            }
        }
    }
}
