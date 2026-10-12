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
    private static void SelectedPluginUiResume(bool hidePausedCounters = false)
    {
        foreach (bool delayAck in new[]
        {
            false,
            true
        }

        )
        {
            var calls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher,
                Mode = delayAck ? ApplicationMode.Fullscreen : ApplicationMode.Desktop
            };
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
            using (var first = new SelectionFixture(550))
            using (var resumed = new SelectionFixture(550))
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            using (var firstReturned = new ManualResetEvent(false))
            using (var resumedReturned = new ManualResetEvent(false))
            {
                first.HidePausedCounters = hidePausedCounters;
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first } });
                int opens = 0;
                SetField(LaunchCoordinatorFor(plugin), "selectedSessionFactory", new Func<LaunchIdentity, IUpdateControlSession>(id => Interlocked.Increment(ref opens) == 1 ? first.Session : resumed.Session));
                var game = SelectedGame(550);
                calls.DatabaseGame = game;
                var startingCopy = game.GetCopy();
                var args = Event<OnGameStartingEventArgs>(startingCopy);
                OnGameStartingEventArgs resumedArgs = null;
                calls.OnStartGame = id =>
                {
                    Eq(game.Id, id);
                    Eq(false, game.IsLaunching);
                    Eq(true, startingCopy.IsLaunching);
                    game.IsLaunching = true;
                    resumed.Enabled = true;
                    resumedArgs = Event<OnGameStartingEventArgs>(game.GetCopy());
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            plugin.OnGameStarting(resumedArgs);
                        }
                        finally
                        {
                            resumedReturned.Set();
                        }
                    });
                };
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(args);
                    }
                    finally
                    {
                        firstReturned.Set();
                    }
                });
                try
                {
                    Until(() => plugin.Monitor.Current.CanPause);
                    long oldToken = plugin.Monitor.Current.SessionId;
                    Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                    Eq(true, firstReturned.WaitOne(1500));
                    Eq(true, args.CancelStartup);
                    Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                    Eq(0, first.Removals);
                    Eq(0, calls.Starts);
                    if (!delayAck)
                        AcknowledgeSdkCancellation(plugin, args, game);
                    if (delayAck)
                    {
                        first.Complete = true;
                        resumed.Complete = true;
                    }

                    Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                    Until(() => plugin.Monitor.Current.CanPause && !plugin.Monitor.Current.CommandPending);
                    if (delayAck)
                    {
                        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                        {
                        }));
                        Eq(0, calls.Starts);
                        plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(game));
                        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                        {
                        }));
                        Eq(0, calls.Starts);
                        game.IsLaunching = false;
                    }

                    if (!delayAck)
                    {
                        PumpUntil(() => calls.Starts == 1 && resumedArgs != null);
                        Eq(true, game.IsLaunching);
                        Eq(false, resumedReturned.WaitOne(0));
                        first.Complete = true;
                    }

                    PumpUntil(() => calls.Starts == 1 && resumedArgs != null && resumedReturned.WaitOne(0));
                    Eq(oldToken, plugin.Monitor.Current.SessionId);
                    Eq(1, opens);
                    Eq(2, first.Commands);
                    Eq(0, first.Removals);
                    Eq(0, resumed.Removals);
                    Eq(0, resumed.Selections);
                    Eq(true, game.IsLaunching);
                    Eq(false, resumedArgs.CancelStartup);
                    Eq(1, calls.Starts);
                    Eq(0, calls.LibraryReturns + calls.FullscreenToggles);
                }
                finally
                {
                    plugin.Dispose();
                    firstReturned.WaitOne(1500);
                    resumedReturned.WaitOne(1500);
                }
            }
        }
    }

    private static void SelectedResumeUiReplacement()
    {
        foreach (ApplicationMode mode in new[]
        {
            ApplicationMode.Desktop,
            ApplicationMode.Fullscreen
        }

        )
        {
            var calls = new ApiCalls
            {
                Dispatcher = Dispatcher.CurrentDispatcher,
                Mode = mode
            };
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
            using (var first = new SelectionFixture(550))
            using (var resumed = new SelectionFixture(550))
            using (var next = new SelectionFixture(551))
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            using (var firstReturned = new ManualResetEvent(false))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next } });
                int opens = 0;
                SetField(LaunchCoordinatorFor(plugin), "selectedSessionFactory", new Func<LaunchIdentity, IUpdateControlSession>(id => id.LauncherId == "551" ? next.Session : Interlocked.Increment(ref opens) == 1 ? first.Session : resumed.Session));
                var game = SelectedGame(550);
                var newGame = SelectedGame(551);
                calls.DatabaseGame = game;
                var args = Event<OnGameStartingEventArgs>(game.GetCopy());
                calls.OnGetGame = id => id == game.Id ? game : id == newGame.Id ? newGame : null;
                int actualLaunches = 0;
                calls.OnStartGame = id =>
                {
                    var launching = calls.OnGetGame(id);
                    Eq(false, launching.IsLaunching);
                    launching.IsLaunching = true;
                    if (id == newGame.Id)
                    {
                        Eq(false, game.IsLaunching);
                        Eq(true, first.Removed);
                    }

                    var callback = Event<OnGameStartingEventArgs>(launching.GetCopy());
                    // The installed SDK sends synchronously to its UI context.
                    plugin.OnGameStarting(callback);
                    if (callback.CancelStartup)
                        AcknowledgeSdkCancellation(plugin, callback, launching);
                    else
                    {
                        Eq(newGame.Id, id);
                        actualLaunches++;
                    }
                };
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        plugin.OnGameStarting(args);
                    }
                    finally
                    {
                        firstReturned.Set();
                    }
                });
                var nextArgs = Event<OnGameStartingEventArgs>(newGame);
                bool replacement = false, inspected = false, oldLaunching = false;
                Exception error = null;
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(25)
                };
                var inspector = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(25)
                };
                var deadline = DateTime.UtcNow.AddSeconds(12);
                DateTime resumedAt = DateTime.MaxValue;
                timer.Tick += (s, e) =>
                {
                    try
                    {
                        var state = plugin.Monitor.Current;
                        if (!replacement && state.GameId == game.Id && state.CanPause && !state.CommandPending)
                        {
                            if (resumedAt == DateTime.MaxValue)
                            {
                                resumedAt = DateTime.UtcNow;
                                return;
                            }

                            if (DateTime.UtcNow - resumedAt < TimeSpan.FromMilliseconds(250))
                                return;
                            Eq(true, game.IsLaunching);
                            replacement = true;
                            timer.Stop();
                            plugin.OnGameStarting(nextArgs);
                            AcknowledgeSdkCancellation(plugin, nextArgs, newGame);
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                        timer.Stop();
                        plugin.Dispose();
                    }
                };
                inspector.Tick += (s, e) =>
                {
                    var state = plugin.Monitor.Current;
                    if (replacement && !inspected && state.GameId == newGame.Id && state.CanPause)
                    {
                        inspected = true;
                        oldLaunching = game.IsLaunching;
                        Eq(true, newGame.IsLaunching);
                        next.Complete = true;
                        inspector.Stop();
                    }
                    else if (DateTime.UtcNow > deadline)
                    {
                        error = new Exception("UI replacement fixture timed out");
                        inspector.Stop();
                        timer.Stop();
                        plugin.Dispose();
                    }
                };
                try
                {
                    Until(() => plugin.Monitor.Current.CanPause);
                    Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                    Eq(true, firstReturned.WaitOne(1500));
                    AcknowledgeSdkCancellation(plugin, args, game);
                    Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                    Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                    inspector.Start();
                    timer.Start();
                    PumpUntil(() => inspected || error != null);
                    if (error != null)
                        throw error;
                    Eq(true, replacement);
                    Eq(false, oldLaunching);
                    Eq(false, game.IsLaunching);
                    Eq(true, nextArgs.CancelStartup);
                    Eq(2, calls.Starts);
                    Eq(1, actualLaunches);
                    Eq(1, first.Removals);
                    Eq(0, resumed.Selections);
                    Eq(0, calls.LibraryReturns + calls.FullscreenToggles);
                    first.Complete = true;
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                    {
                    }));
                    Eq(2, calls.Starts);
                }
                finally
                {
                    timer.Stop();
                    inspector.Stop();
                    plugin.Dispose();
                    firstReturned.WaitOne(1500);
                }
            }
        }
    }

    private static void SelectedResumeUiCompletion()
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
            int opens = 0;
            SetField(LaunchCoordinatorFor(plugin), "selectedSessionFactory", new Func<LaunchIdentity, IUpdateControlSession>(id =>
            {
                if (++opens != 1)
                    throw new Exception("A completed resume must not reopen/reselect its update");
                return first.Session;
            }));
            var game = SelectedGame(550);
            calls.DatabaseGame = game;
            var args = Event<OnGameStartingEventArgs>(game.GetCopy());
            bool released = false, inspected = false;
            calls.OnStartGame = id =>
            {
                Eq(true, Dispatcher.CurrentDispatcher == calls.Dispatcher);
                Eq(game.Id, id);
                Eq(UpdatePhase.Updating, plugin.Monitor.Current.Phase);
                game.IsLaunching = true;
                var callback = Event<OnGameStartingEventArgs>(game.GetCopy());
                plugin.OnGameStarting(callback);
                Eq(false, callback.CancelStartup);
                released = true;
            };
            var inspector = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            var deadline = DateTime.UtcNow.AddSeconds(8);
            Exception error = null;
            inspector.Tick += (s, e) =>
            {
                try
                {
                    if (game.IsLaunching && calls.Starts == 1 && plugin.Monitor.Current.CanPause)
                    {
                        inspector.Stop();
                        long token = plugin.Monitor.Current.SessionId;
                        var browsing = SelectedGame(551);
                        plugin.OnGameSelected(new OnGameSelectedEventArgs(new List<Game> { game }, new List<Game> { browsing }));
                        Eq(true, game.IsLaunching);
                        Eq(game.Id, plugin.Monitor.Current.GameId);
                        Eq(token, plugin.Monitor.Current.SessionId);
                        Eq(0, first.Removals);
                        Eq(false, released);
                        inspected = true;
                        first.Complete = true;
                    }
                    else if (DateTime.UtcNow > deadline)
                        throw new Exception("Resumed launching fixture timed out");
                }
                catch (Exception ex)
                {
                    error = ex;
                    inspector.Stop();
                    plugin.Dispose();
                }
            };
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
                Until(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                Eq(true, returned.WaitOne(1500));
                AcknowledgeSdkCancellation(plugin, args, game);
                Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                long token = plugin.Monitor.Current.SessionId;
                inspector.Start();
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                PumpUntil(() => released || error != null);
                if (error != null)
                    throw error;
                Eq(true, inspected);
                Eq(1, calls.Starts);
                Eq(1, opens);
                Eq(1, first.Selections);
                Eq(token, plugin.Monitor.Current.SessionId);
                Eq(2, first.Commands);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                Eq(1, calls.Starts);
            }
            finally
            {
                inspector.Stop();
                plugin.Dispose();
                returned.WaitOne(1500);
            }
        }
    }

    private static void SelectedPausedCloseAndReplacement()
    {
        foreach (bool close in new[]
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
            using (var returned = new ManualResetEvent(false))
            using (var nextReturned = new ManualResetEvent(false))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next } });
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
                    Until(() => plugin.Monitor.Current.CanPause);
                    Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                    Eq(true, returned.WaitOne(1500));
                    AcknowledgeSdkCancellation(plugin, args, game);
                    Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                    if (close)
                    {
                        Eq(true, plugin.Monitor.RequestClose(game.Id, plugin.Monitor.Current.SessionId));
                        PumpUntil(() => plugin.Monitor.Current.Phase == UpdatePhase.Idle);
                        Eq(false, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                    }
                    else
                    {
                        var newGame = SelectedGame(551);
                        var nextArgs = Event<OnGameStartingEventArgs>(newGame);
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
                        Eq(false, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                        next.Complete = true;
                        Eq(true, nextReturned.WaitOne(2500));
                        Eq(false, nextArgs.CancelStartup);
                    }

                    Eq(1, first.Removals);
                    Eq(true, args.CancelStartup);
                    Eq(0, calls.Starts);
                }
                finally
                {
                    plugin.Dispose();
                    returned.WaitOne(1500);
                    if (!close)
                        nextReturned.WaitOne(1500);
                }
            }
        }
    }

    private static void SelectedLateResume()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var first = new SelectionFixture(550))
        using (var next = new SelectionFixture(551))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        using (var nextReturned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next } });
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
                Until(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                Eq(true, returned.WaitOne(1500));
                AcknowledgeSdkCancellation(plugin, args, game);
                Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                PumpUntil(() => calls.Starts == 1);
                var newGame = SelectedGame(551);
                var nextArgs = Event<OnGameStartingEventArgs>(newGame);
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
                var late = Event<OnGameStartingEventArgs>(game);
                plugin.OnGameStarting(late);
                Eq(true, late.CancelStartup);
                Eq(newGame.Id, plugin.Monitor.Current.GameId);
                Eq(0, next.Removals);
                Eq(1, first.Removals);
                plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(game));
                Eq(newGame.Id, plugin.Monitor.Current.GameId);
                next.Complete = true;
                Eq(true, nextReturned.WaitOne(2500));
                Eq(false, nextArgs.CancelStartup);
                Eq(1, calls.Starts);
            }
            finally
            {
                plugin.Dispose();
                returned.WaitOne(1500);
                nextReturned.WaitOne(1500);
            }
        }
    }

    private static void SelectedResumeFailure()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            OnStartGame = id =>
            {
                throw new InvalidOperationException("SDK fixture refusal");
            }
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var first = new SelectionFixture(550))
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first } });
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
                Until(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                Eq(true, returned.WaitOne(1500));
                AcknowledgeSdkCancellation(plugin, args, game);
                Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                first.Complete = true;
                PumpUntil(() => calls.Starts == 1 && plugin.Monitor.Current.CommandNotice == "Unable to restart — press Play again");
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                Eq(1, calls.Starts);
                Eq(true, args.CancelStartup);
                Eq(false, plugin.Monitor.Current.Dismissed);
                Eq(0, first.Removals);
            }
            finally
            {
                plugin.Dispose();
                returned.WaitOne(1500);
            }
        }
    }

    private static void SelectedResumeDatabaseGuards()
    {
        foreach (bool running in new[]
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
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            using (var returned = new ManualResetEvent(false))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first } });
                var game = SelectedGame(550);
                calls.DatabaseGame = game;
                var copy = game.GetCopy();
                var args = Event<OnGameStartingEventArgs>(copy);
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
                    Until(() => plugin.Monitor.Current.CanPause);
                    Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Pause));
                    Eq(true, returned.WaitOne(1500));
                    AcknowledgeSdkCancellation(plugin, args, game);
                    Until(() => plugin.Monitor.Current.CanResume && !plugin.Monitor.Current.CommandPending);
                    if (running)
                    {
                        var current = game.GetCopy();
                        current.IsRunning = true;
                        calls.DatabaseGame = current;
                    }
                    else
                        calls.DatabaseGame = null;
                    first.Complete = true;
                    Eq(true, plugin.Monitor.RequestCommand(game.Id, UpdateCommand.Resume));
                    PumpUntil(() => plugin.Monitor.Current.Phase == UpdatePhase.Ready && !plugin.Monitor.Current.CommandPending);
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                    {
                    }));
                    Eq(0, calls.Starts);
                    Eq(true, copy.IsLaunching);
                    Eq(true, args.CancelStartup);
                    if (!running)
                        Eq("Unable to restart — press Play again", plugin.Monitor.Current.CommandNotice);
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
