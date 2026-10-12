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
    private sealed class NormalSession : IUpdateControlSession
    {
        public readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false), Finished = new ManualResetEvent(false);
        public readonly UpdatePhase Phase;
        public int Commands;
        public NormalSession(UpdatePhase phase)
        {
            Phase = phase;
        }

        public UpdateObservation Poll()
        {
            Entered.Set();
            Release.WaitOne(4000);
            Finished.Set();
            return new UpdateObservation
            {
                Phase = Phase,
                CanResume = Phase == UpdatePhase.Updating,
                Display = new UpdateDisplay
                {
                    Paused = true,
                    Stage = "Paused"
                },
                Detail = Phase == UpdatePhase.Error ? "Saved update error 6" : null
            };
        }

        public bool SendCommand(UpdateCommand command)
        {
            Interlocked.Increment(ref Commands);
            return true;
        }

        public void Dispose()
        {
            Release.Set();
        }
    }

    private sealed class NormalAdapter : ILauncherUpdateAdapter
    {
        private readonly IUpdateSession session;
        public string Name
        {
            get
            {
                return "Normal Play fixture";
            }
        }

        public NormalAdapter(IUpdateSession session)
        {
            this.session = session;
        }

        public IUpdateSession Open(LaunchIdentity identity)
        {
            return session;
        }
    }

    private static void NormalPlay(UpdatePhase phase)
    {
        var calls = new ApiCalls
        {
            Dispatcher = Dispatcher.CurrentDispatcher
        };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            var session = new NormalSession(phase);
            var game = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Normal callback fixture"
            };
            var coordinator = LaunchCoordinatorFor(plugin);
            var registry = (LauncherRegistry)coordinator.GetType().GetField("launchers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(coordinator);
            registry.Register(new LauncherRegistration("normal-fixture", new NormalAdapter(session), g => g.Id == game.Id ? new LaunchIdentity { GameId = g.Id, LauncherId = "fixture", Name = g.Name } : null, null, null, UpdateCapabilities.Phases));
            var args = Event<OnGameStartingEventArgs>(game);
            var action = new GameAction
            {
                Path = "original-play-action",
                IsPlayAction = true
            };
            typeof(OnGameStartingEventArgs).GetProperty("SourceAction").SetValue(args, action, null);
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
                // The observer is intentionally blocked. The original Play callback
                // must still return; it must never read/wait/cancel on that data.
                Eq(true, returned.WaitOne(1500));
                Eq(false, args.CancelStartup);
                Eq(action, args.SourceAction);
                Eq(true, session.Entered.WaitOne(1000));
                Eq(0, session.Commands);
                Eq(0, calls.Starts);
                session.Release.Set();
                Until(() => plugin.Monitor.Current.Phase == phase);
                Eq(false, args.CancelStartup);
                Eq(0, session.Commands);
                Eq(0, calls.Starts);
            }
            finally
            {
                session.Release.Set();
                plugin.Monitor.Stop(game.Id);
                returned.WaitOne(5000);
                session.Finished.WaitOne(1000);
            }
        }
    }
}
