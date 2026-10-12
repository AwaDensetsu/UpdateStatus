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
    public sealed class LibraryFixture
    {
        public int StatusCloses, DetailsCloses, ConfirmationPrompts, SdkCancellations;
        public bool GameDetailsVisible { get; private set; }
        public bool GameStatusVisible { get; set; }
        public System.Windows.Input.ICommand CloseGameStatusCommand { get; private set; }
        public System.Windows.Input.ICommand ToggleGameDetailsCommand { get; private set; }

        public LibraryFixture(Game game = null)
        {
            GameDetailsVisible = GameStatusVisible = true;
            CloseGameStatusCommand = new RelayCommand<object>(_ =>
            {
                if (game != null && (game.IsLaunching || game.IsRunning))
                {
                    ConfirmationPrompts++;
                    throw new Exception("Cancel game monitoring?");
                }

                StatusCloses++;
                GameStatusVisible = false;
            });
            ToggleGameDetailsCommand = new RelayCommand<object>(_ =>
            {
                DetailsCloses++;
                GameDetailsVisible = false;
            });
        }

        public void ToggleSdkDetails()
        {
            DetailsCloses++;
            GameDetailsVisible = !GameDetailsVisible;
        }

        public void SdkStartupCancelled()
        {
            SdkCancellations++;
            GameStatusVisible = false;
        }
    }

    private static void CloseLibrary()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher,
            Mode = ApplicationMode.Fullscreen
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        {
            var id = Guid.NewGuid();
            var session = new ControlSession();
            var game = new Game
            {
                Id = id,
                IsLaunching = true
            };
            calls.DatabaseGame = game;
            plugin.Monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => plugin.Monitor.Current.CanPause);
            var model = new LibraryFixture(game);
            var previousMain = Application.Current.MainWindow;
            calls.FullscreenView = FullscreenView.Details;
            calls.OnFullscreenToggle = model.ToggleSdkDetails;
            var window = new TestWindow
            {
                Width = 660,
                Height = 450,
                DataContext = model,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                Application.Current.MainWindow = window;
                window.Show();
                Eq(true, plugin.Monitor.RequestClose(id, plugin.Monitor.Current.SessionId));
                Until(() => session.Calls == 1);
                session.Paused = true;
                PumpUntil(() => plugin.Monitor.Current.Phase == UpdatePhase.Idle && calls.FullscreenToggles == 1);
                // The normal callback was already forwarded: closing this observer
                // cannot alter host execution tracking through the public SDK.
                Eq(true, game.IsLaunching);
                Eq(0, model.ConfirmationPrompts);
                Eq(0, model.SdkCancellations);
                Eq(0, model.StatusCloses);
                Eq(1, model.DetailsCloses);
                Eq(0, calls.LibraryReturns);
                Eq(0, calls.Starts);
                Thread.Sleep(600);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                Eq(0, model.StatusCloses);
                Eq(1, calls.FullscreenToggles);
                Eq(1, session.Calls);
                Eq(true, session.Disposed);
            }
            finally
            {
                window.Close();
                Application.Current.MainWindow = previousMain;
            }
        }
    }

    private static void AcknowledgeSdkCancellation(PlayniteUpdateStatus.Plugin plugin, OnGameStartingEventArgs args, Game game, LibraryFixture model = null)
    {
        if (!args.CancelStartup)
            return;
        // Simulate the host's documented callback check and cancellation event.
        // Its own cleanup follows the notification; the plugin never edits flags.
        plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(game));
        game.IsLaunching = false;
        if (model != null)
            model.SdkStartupCancelled();
    }

    private static void SilentCloseGuards()
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        var game = new Game
        {
            Id = Guid.NewGuid(),
            IsLaunching = true,
            IsRunning = true
        };
        calls.DatabaseGame = game;
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        {
            var session = new ControlSession();
            plugin.Monitor.Start(new LaunchIdentity { GameId = game.Id }, session, 500);
            Until(() => plugin.Monitor.Current.CanPause);
            Eq(true, plugin.Monitor.RequestClose(game.Id, plugin.Monitor.Current.SessionId));
            Until(() => session.Calls == 1);
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
            {
            }));
            Eq(0, calls.LibraryReturns);
            session.Paused = true;
            PumpUntil(() => plugin.Monitor.Current.Phase == UpdatePhase.Idle && calls.LibraryReturns == 1);
            Eq(true, game.IsLaunching);
            Eq(true, game.IsRunning);
            Eq(0, calls.Starts);
        }

        // No view-model members are available. Public SDK navigation still works.
        calls.Mode = ApplicationMode.Fullscreen;
        calls.FullscreenView = FullscreenView.Details;
        LibraryNavigation.Return(api);
        Eq(1, calls.FullscreenToggles);
        Eq(FullscreenView.List, calls.FullscreenView);
        LibraryNavigation.Return(api);
        Eq(1, calls.FullscreenToggles);
    }
}
