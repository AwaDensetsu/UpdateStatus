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
    private static void SelectedSerializedUiPlay(int scenario)
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
            using (var next = new SelectionFixture(551))
            using (var latest = new SelectionFixture(552))
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, first }, { 551, next }, { 552, latest } });
                var a = SelectedGame(550);
                var b = SelectedGame(551);
                var c = SelectedGame(552);
                a.IsLaunching = b.IsLaunching = c.IsLaunching = false;
                calls.OnGetGame = id => id == a.Id ? a : id == b.Id ? b : id == c.Id ? c : null;
                if (scenario == 1 || scenario == 4)
                    first.RemovalConfirmed = false;
                if (scenario == 2)
                    first.RemovalAccept = false;
                int actualLaunches = 0, callbackDepth = 0, maxDepth = 0, step = 0;
                bool done = false;
                Exception error = null;
                Action<Game> hostPlay = game =>
                {
                    Eq(false, game.IsLaunching);
                    game.IsLaunching = true;
                    if (game.Id != a.Id && calls.Starts != 0)
                    {
                        Eq(false, a.IsLaunching);
                        Eq(true, first.Removed);
                    }

                    var args = Event<OnGameStartingEventArgs>(game.GetCopy());
                    callbackDepth++;
                    maxDepth = Math.Max(maxDepth, callbackDepth);
                    try
                    {
                        plugin.OnGameStarting(args);
                    }
                    finally
                    {
                        callbackDepth--;
                    }

                    if (args.CancelStartup)
                    {
                        if (scenario == 6 && game.Id == a.Id)
                            plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(game));
                        else
                            AcknowledgeSdkCancellation(plugin, args, game);
                    }
                    else
                    {
                        actualLaunches++;
                        done = true;
                    }
                };
                calls.OnStartGame = id =>
                {
                    if (scenario == 5)
                        throw new InvalidOperationException("Replacement SDK fixture refusal");
                    if (scenario == 7 && id == b.Id)
                        return;
                    hostPlay(calls.OnGetGame(id));
                };
                var actor = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(25)
                };
                var watchdog = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(25)
                };
                var deadline = DateTime.UtcNow.AddSeconds(12);
                DateTime? cleanupWait = null;
                actor.Tick += (s, e) =>
                {
                    try
                    {
                        var state = plugin.Monitor.Current;
                        if (state.GameId == a.Id && !state.CommandPending)
                        {
                            if (scenario == 3)
                            {
                                if ((step == 0 || step == 2) && state.CanPause && a.IsLaunching)
                                {
                                    step++;
                                    Eq(true, plugin.Monitor.RequestCommand(a.Id, UpdateCommand.Pause));
                                }
                                else if ((step == 1 || step == 3) && state.CanResume && !a.IsLaunching)
                                {
                                    step++;
                                    Eq(true, plugin.Monitor.RequestCommand(a.Id, UpdateCommand.Resume));
                                }
                                else if (step == 4 && state.CanPause && a.IsLaunching)
                                {
                                    step++;
                                    Eq(true, plugin.Monitor.RequestClose(a.Id, state.SessionId));
                                }
                            }
                            else if (step == 0 && state.CanPause && a.IsLaunching)
                            {
                                step++;
                                hostPlay(b);
                            }
                        }

                        if (scenario == 1 && step == 1 && first.Removals == 1 && !a.IsLaunching && !b.IsLaunching)
                        {
                            step++;
                            hostPlay(c);
                            first.RemovalConfirmed = true;
                        }

                        if (scenario == 4 && step == 1 && first.Removals == 1 && !a.IsLaunching && !b.IsLaunching)
                        {
                            step++;
                            plugin.Settings.Enabled = false;
                            plugin.ApplySettings();
                            first.RemovalConfirmed = true;
                        }

                        if (scenario == 6 && step == 1 && first.Removed && !state.CommandPending)
                        {
                            Eq(0, calls.Starts);
                            Eq(true, a.IsLaunching);
                            if (!cleanupWait.HasValue)
                                cleanupWait = DateTime.UtcNow;
                            if (DateTime.UtcNow - cleanupWait.Value > TimeSpan.FromMilliseconds(250))
                            {
                                step++;
                                a.IsLaunching = false;
                            }
                        }

                        if (scenario == 7 && step == 1 && calls.Starts == 1 && !a.IsLaunching && !b.IsLaunching)
                        {
                            step++;
                            hostPlay(c);
                        }

                        Guid target = scenario == 1 || scenario == 7 ? c.Id : b.Id;
                        if (state.GameId == target && state.CanPause && !state.CommandPending)
                        {
                            Eq(false, a.IsLaunching);
                            Eq(true, calls.OnGetGame(target).IsLaunching);
                            Eq(1, callbackDepth); // the cancelled A stack is fully gone
                            if (scenario == 1 || scenario == 7)
                            {
                                Eq(false, b.IsLaunching);
                                Eq(0, next.Selections);
                                latest.Complete = true;
                            }
                            else
                                next.Complete = true;
                            actor.Stop();
                        }

                        if (scenario == 2 && first.Removals == 1 && !state.CommandPending && !a.IsLaunching && !b.IsLaunching)
                        {
                            done = true;
                            actor.Stop();
                        }

                        if ((scenario == 3 || scenario == 4) && state.Phase == UpdatePhase.Idle && !a.IsLaunching && !b.IsLaunching)
                        {
                            done = true;
                            actor.Stop();
                        }

                        if (scenario == 5 && state.CommandNotice == "Unable to restart — press Play again")
                        {
                            done = true;
                            actor.Stop();
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                        actor.Stop();
                        plugin.Dispose();
                    }
                };
                watchdog.Tick += (s, e) =>
                {
                    try
                    {
                        var state = plugin.Monitor.Current;
                        if (state.GameId != a.Id && state.CanPause)
                        {
                            Eq(false, a.IsLaunching);
                            Eq(1, callbackDepth);
                        }

                        if (scenario == 7 && step == 2 && state.GameId == c.Id && state.CanPause)
                        {
                            step++;
                            hostPlay(b);
                            Eq(false, b.IsLaunching);
                            Eq(c.Id, plugin.Monitor.Current.GameId);
                            Eq(true, c.IsLaunching);
                            Eq(0, next.Selections);
                            latest.Complete = true;
                        }

                        if (DateTime.UtcNow > deadline && !done)
                            throw new Exception("Serialized UI scenario timed out: " + scenario + ", step=" + step);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                        actor.Stop();
                        watchdog.Stop();
                        plugin.Dispose();
                    }
                };
                try
                {
                    actor.Start();
                    watchdog.Start();
                    hostPlay(a);
                    PumpUntil(() => done || error != null);
                    if (error != null)
                        throw error;
                    Eq(false, a.IsLaunching);
                    Eq(1, first.Removals);
                    Eq(scenario == 0 || scenario == 1 || scenario == 6 || scenario == 7 ? 1 : 0, actualLaunches);
                    Eq(scenario == 3 ? 2 : scenario == 2 || scenario == 4 ? 0 : 1, calls.Starts);
                    Eq(scenario == 0 || scenario == 6 ? 1 : 0, next.Selections);
                    Eq(scenario == 1 || scenario == 7 ? 1 : 0, latest.Selections);
                    // Brief nested callbacks cancel immediately; only one long
                    // retained host callback exists at a time (asserted above).
                    Eq(true, maxDepth <= 2);
                    first.Complete = true;
                    first.Enabled = true;
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                    {
                    }));
                    Eq(scenario == 3 ? 2 : scenario == 2 || scenario == 4 ? 0 : 1, calls.Starts);
                }
                finally
                {
                    actor.Stop();
                    watchdog.Stop();
                    plugin.Dispose();
                }
            }
        }
    }
}
