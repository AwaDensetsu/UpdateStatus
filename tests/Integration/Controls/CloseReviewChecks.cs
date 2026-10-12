using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Runtime.Remoting.Proxies;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.UI;

internal static partial class Program
{
    private static void CloseReviewTests()
    {
        Test("failed Close with fresh data returns the retained Desktop SDK callback", () => FailedCloseWithFreshData(ApplicationMode.Desktop));
        Test("failed Close with fresh data returns the retained Fullscreen SDK callback", () => FailedCloseWithFreshData(ApplicationMode.Fullscreen));
        Test("the update card disables Close while controls are disabled", DisabledCloseControl);
    }

    private static void FailedCloseWithFreshData(ApplicationMode mode)
    {
        var calls = new ApiCalls { Dispatcher = Dispatcher.CurrentDispatcher, Mode = mode };
        var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI), calls).GetTransparentProxy();
        using (var fixture = new SelectionFixture(550) { RemovalAccept = false })
        using (var plugin = new PlayniteUpdateStatus.Plugin(api))
        using (var returned = new ManualResetEvent(false))
        {
            ConfigureSelection(plugin, new Dictionary<uint, SelectionFixture> { { 550, fixture } });
            fixture.Observer.FreshReceipt = true;
            var game = SelectedGame(550);
            var args = Event<OnGameStartingEventArgs>(game);
            ThreadPool.QueueUserWorkItem(_ => {
                try { plugin.OnGameStarting(args); AcknowledgeSdkCancellation(plugin, args, game); }
                finally { returned.Set(); }
            });
            try
            {
                PumpUntil(() => plugin.Monitor.Current.CanPause);
                Eq(true, plugin.Monitor.RequestClose(game.Id, plugin.Monitor.Current.SessionId));
                PumpUntil(() => fixture.Removals == 1 && !plugin.Monitor.Current.CommandPending);
                Eq(true, returned.WaitOne(2000));
                Eq(true, args.CancelStartup);
                Eq(false, game.IsLaunching);
                Eq(true, fixture.Enabled);
                Eq(false, fixture.Removed);
                Eq(false, plugin.Monitor.Current.Dismissed);
                Eq(UpdatePhase.Updating, plugin.Monitor.Current.Phase);
                Eq(0, calls.LibraryReturns + calls.FullscreenToggles);
                fixture.Observer.Phase = UpdatePhase.Ready;
                PumpUntil(() => plugin.Monitor.Current.Display != null && plugin.Monitor.Current.Display.Stage == "Update complete");
                Eq(UpdatePhase.Updating, plugin.Monitor.Current.Phase);
                Eq(0, calls.Starts);
                Eq(1, fixture.Selections);
                Eq(1, fixture.Removals);
            }
            finally { plugin.Dispose(); Eq(true, returned.WaitOne(2500)); }
        }
    }

    private static void DisabledCloseControl()
    {
        using (var monitor = new UpdateMonitor((s,w) => { }))
        {
            monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, new ControlSession(), 500);
            Until(() => monitor.Current.CanPause);
            var card = new StatusControl(monitor, true, true, true);
            card.Render(monitor.Current);
            var panel = (StackPanel)((Border)card.Content).Child;
            var close = panel.Children.OfType<Grid>().Single().Children.OfType<Button>().Single();
            Eq(true, close.IsEnabled);
            monitor.ControlsEnabled = false;
            card.Render(monitor.Current);
            Eq(false, close.IsEnabled);
            monitor.ControlsEnabled = true;
            card.Render(monitor.Current);
            Eq(true, close.IsEnabled);
        }
    }
}
