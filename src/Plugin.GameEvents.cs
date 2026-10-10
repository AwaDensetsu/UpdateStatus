using Playnite.SDK.Events;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            if (closing || stopped) { args.CancelStartup = true; return; }
            launchCoordinator.OnGameStarting(args);
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (!closing && !stopped) launchCoordinator.OnGameStarted(args);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (!closing && !stopped) launchCoordinator.OnGameStopped(args);
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            if (!closing && !stopped) launchCoordinator.OnGameStartupCancelled(args);
        }
    }
}
