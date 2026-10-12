using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        public string SelectSteamFolder() { return PlayniteApi.Dialogs.SelectFolder(); }
        private void StoreSteamLocation(string folder) { if (!stopped) steamLocation.Remember(folder); }
        public void ApplySettings()
        {
            if (closing || stopped) return;
            bool pathChanged = !string.Equals(SteamLocator.ManualRoot, Settings.SteamFolder, StringComparison.OrdinalIgnoreCase);
            SteamLocator.ManualRoot = Settings.SteamFolder;
            Monitor.SpeedUnit = Settings.SpeedUnit;
            EpicCatalog.Configure(Settings.Enabled && Settings.EpicEnabled && !Settings.EpicConnectionRemoved, Settings.EpicPollingInterval, Settings.EpicFolder);
            if (Settings.SteamConnectionRemoved)
            {
                SteamCatalog.Stop();
                CoverHistory.Clear();
                launchCoordinator.RemoveLauncherObservation("steam");
            }
            if (!Settings.EpicEnabled || Settings.EpicConnectionRemoved)
                launchCoordinator.RemoveLauncherObservation("epic");
            if (pathChanged) SteamCatalog.Stop();
            if (Settings.Enabled && !Settings.SteamConnectionRemoved) SteamCatalog.Start(); else SteamCatalog.Stop();
            if (!Settings.Enabled) Monitor.Stop(Guid.Empty);
            ApplyCoverSettings();
        }
        private bool CoverBadgesEnabled { get { return !closing && !stopped && Settings.Enabled && Settings.CoverBadges; } }
        private void CoverCatalogChanged(SteamCatalogSnapshot catalog) { if (!stopped && !Settings.SteamConnectionRemoved) CoverHistory.Observe(catalog); }
        private void ApplyCoverSettings()
        {
            var dispatcher = PlayniteApi.MainView.UIDispatcher; if (dispatcher.HasShutdownStarted) return;
            Action apply = () => { if (CoverBadgesEnabled) { coverTimer.Start(); RefreshCoverBadges(); } else { coverTimer.Stop(); coverPresenter.Dispose(); RefreshBadgeControls(); } };
            if (dispatcher.CheckAccess()) apply(); else dispatcher.BeginInvoke(apply);
        }
        private void RefreshBadgeControls()
        {
            for (int i = badgeControls.Count - 1; i >= 0; i--)
            {
                var badge = badgeControls[i].Target as CoverBadgeControl;
                if (badge == null) badgeControls.RemoveAt(i);
                else badge.Refresh();
            }
        }
        private void ShutdownBadgeControls()
        {
            foreach (var item in badgeControls)
            {
                var badge = item.Target as CoverBadgeControl;
                if (badge != null) badge.Shutdown();
            }
            badgeControls.Clear();
        }
        private CoverBadgeInfo EpicBadge(Playnite.SDK.Models.Game game) => Settings.EpicEnabled && !Settings.EpicConnectionRemoved ? EpicCatalog.Current.Badge(game) : null;
        private SteamCatalogSnapshot SteamBadgeCatalog => Settings.SteamConnectionRemoved ? null : SteamCatalog.Current;
        private SteamCoverHistory SteamBadgeHistory => Settings.SteamConnectionRemoved ? null : CoverHistory;
        private UpdateState SteamBadgeSelection => Settings.SteamConnectionRemoved ? null : Monitor.Current;
        private void RefreshCoverBadges()
        {
            try { coverPresenter.Refresh(Application.Current == null ? null : Application.Current.MainWindow, SteamBadgeCatalog, CoverBadgesEnabled, SteamBadgeHistory, SteamBadgeSelection, EpicBadge); RefreshBadgeControls(); }
            catch (Exception e) { coverPresenter.Dispose(); Log("Cover badges unavailable for this view: " + e.Message, true); }
        }
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            if (Settings.Enabled)
                yield return new MainMenuItem { MenuSection = "@Update Status", Description = Settings.CoverBadges ? "Hide update indicators" : "Show update indicators", Action = _ => { Settings.CoverBadges = !Settings.CoverBadges; settingsViewModel.EndEdit(); } };
            var state = Monitor.Current;
            if (!Settings.Enabled || !state.Visible) yield break;
            if (state.Dismissed) yield return new MainMenuItem { MenuSection = "@Update Status", Description = "Show update status", Action = _ => Monitor.SetStatusDismissed(state.GameId, state.SessionId, false) };
            if (state.Phase != UpdatePhase.Updating || state.CommandPending) yield break;
            if (state.CanPause) yield return new MainMenuItem { MenuSection = "@Update Status", Description = "Pause", Action = _ => Monitor.RequestCommand(state.GameId, UpdateCommand.Pause) };
            if (state.CanResume) yield return new MainMenuItem { MenuSection = "@Update Status", Description = "Resume", Action = _ => Monitor.RequestCommand(state.GameId, UpdateCommand.Resume) };
        }
        public override ISettings GetSettings(bool firstRunSettings) { return settingsViewModel; }
        public override UserControl GetSettingsView(bool firstRunSettings) { return new SettingsView(settingsViewModel); }
        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name == "Badge")
            {
                var badge = new CoverBadgeControl(() => SteamBadgeCatalog, () => CoverBadgesEnabled, () => SteamBadgeHistory, () => SteamBadgeSelection, EpicBadge);
                badgeControls.Add(new WeakReference(badge));
                return badge;
            }
            if (args.Name != "Status") return null;
            var control = new StatusControl(Monitor, false, false, true);
            themeControls.Add(new WeakReference(control));
            return control;
        }
    }
}
