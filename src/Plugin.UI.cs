using System;
using System.Windows;
using Playnite.SDK;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.UI;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        private void StartStatusUI()
        {
            PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() =>
            {
                if (!closing && !stopped && Monitor.Current.Visible) uiTimer.Start();
            }));
        }

        private void StopStatusUI()
        {
            uiTimer.Stop();
            universal.Dispose();
        }

        private void StateChanged(UpdateState state)
        {
            if (closing || stopped) return;
            var dispatcher = PlayniteApi.MainView.UIDispatcher;
            if (dispatcher.HasShutdownStarted || stopped) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (closing || stopped || state.Revision != Monitor.Current.Revision) return;
                if (!state.Visible) { StopStatusUI(); launchCoordinator.ClearObservation(); foregroundPolicy.Reset(); Log("Observer and status controls cleared", false); }
                else if (state.Dismissed)
                {
                    StopStatusUI(); foregroundPolicy.Reset();
                    launchCoordinator.Dismiss(state);
                }
                else { launchCoordinator.ResetLibraryReturn(); uiTimer.Start(); RefreshUI(); }
            }));
        }
        private void RefreshUI()
        {
            if (closing || stopped) return;
            try
            {
                var state = Monitor.Current;
                if (!state.Visible || launchCoordinator.ActiveGame == null || launchCoordinator.ActiveGame.IsRunning) { StopStatusUI(); return; }
                if (state.Dismissed) { StopStatusUI(); foregroundPolicy.Reset(); return; }
                launchCoordinator.PollRecovery(DateTime.UtcNow);
                var window = Application.Current == null ? null : Application.Current.MainWindow;
                if (window == null) return;
                bool fullscreen = PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
                if (state.Phase == UpdatePhase.Updating || state.Phase == UpdatePhase.Ready) launchCoordinator.MarkUpdateObserved();
                Window target = window;
                if (Settings.AutomaticOverlay)
                {
                    target = FindLaunchWindow(window, state.GameId);
                    if ((launchCoordinator.ObservedUpdate || state.Phase == UpdatePhase.Error) && !HasIntegratedControl(target, state)) universal.Refresh(target, state, fullscreen);
                    else universal.Dispose();
                }
                else
                {
                    universal.Dispose();
                }
                if (Settings.KeepFrontendVisible)
                {
                    var launcher = launchCoordinator.ActiveLauncher;
                    string result = launcher == null ? null : FrontendVisibility.RestoreForLauncher(target, state, foregroundPolicy, launcher.Registration.ClientProcesses,
                        () => launchCoordinator.ActiveGame != null && launchCoordinator.ActiveGame.IsRunning || launcher.Registration.IsGameRunning != null && launcher.Registration.IsGameRunning(launcher.Identity));
                    if (result != null && result != foregroundResult) { foregroundResult = result; Log(result, false); }
                }
                else foregroundPolicy.Reset();
            }
            catch (Exception e) { logger.Error(e, "[Update Status] UI integration failed; clearing this launch's status surface."); StopStatusUI(); }
        }
        private bool HasIntegratedControl(Window window, UpdateState state)
        {
            bool found = false;
            for (int i = themeControls.Count - 1; i >= 0; i--)
            {
                var control = themeControls[i].Target as StatusControl;
                if (control == null) { themeControls.RemoveAt(i); continue; }
                if (!control.IsLoaded || Window.GetWindow(control) != window || control.GameContext == null || control.GameContext.Id != state.GameId) continue;
                control.Render(state); found |= control.IsVisible;
            }
            return found;
        }
        private static Window FindLaunchWindow(Window main, Guid gameId)
        {
            if (Application.Current == null) return main;
            foreach (Window window in Application.Current.Windows)
            {
                if (window == main || !window.IsVisible) continue;
                string name = window.GetType().Name;
                if (name.IndexOf("Launch", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("GameStatus", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var property = window.GetType().GetProperty("GameId");
                if (property == null) continue;
                try { if (gameId.Equals(property.GetValue(window, null))) return window; }
                catch (System.Reflection.TargetInvocationException) { }
            }
            return main;
        }
    }
}
