using System;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Threading;

namespace PlayniteUpdateStatus.UI
{
    public partial class SettingsView : UserControl
    {
        private readonly SettingsViewModel model;
        private readonly DispatcherTimer refreshTimer;

        public SettingsView(SettingsViewModel model, Func<string> discovery = null)
        {
            this.model = model ?? throw new ArgumentNullException(nameof(model));
            InitializeComponent();
            RegisterCardNames(SteamCardControl, "Steam");
            RegisterCardNames(EpicCardControl, "Epic");
            model.InitializeLocation(discovery);
            DataContext = model;
            PropertyChangedEventManager.AddHandler(model, ConnectionPresentationChanged, string.Empty);
            UpdateConnectionCards();
            _ = model.RefreshEpicLocationAsync();
            refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromSeconds(2) };
            refreshTimer.Tick += async (s, e) => { model.RefreshSteamLocation(); await model.RefreshEpicLocationAsync(); };
            Loaded += async (s, e) => { model.RefreshSteamLocation(); refreshTimer.Start(); await model.RefreshEpicLocationAsync(); };
            Unloaded += (s, e) => refreshTimer.Stop();
        }

        private void ConnectionPresentationChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(model.SteamCard) && args.PropertyName != nameof(model.EpicCard)) return;
            // Discovery may finish off the UI thread; the view owns dispatcher access.
            if (Dispatcher.CheckAccess()) UpdateConnectionCards();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(UpdateConnectionCards));
        }
        private void UpdateConnectionCards()
        {
            SteamCardControl.State = model.SteamCard;
            EpicCardControl.State = model.EpicCard;
        }

        private void RegisterCardNames(LauncherConnectionCard card, string prefix)
        {
            // Aliases refer to the real shared controls, including existing UI test entry points.
            RegisterName(prefix + "ConnectionPanel", card.FindName("ConnectionPanel"));
            RegisterName(prefix + "ConnectButton", card.FindName("SetupButton"));
            RegisterName(prefix + "RestartButton", card.FindName("RelaunchButton"));
            RegisterName(prefix + "RemoveButton", card.FindName("RemoveButton"));
            RegisterName(prefix + "ConnectionDetails", card.FindName("ConnectionDetails"));
            RegisterName(prefix + "FolderInput", card.FindName("FolderPath"));
            RegisterName(prefix + "FolderStatus", card.FindName("FolderStatus"));
            RegisterName(prefix + "FolderHelp", card.FindName("DescriptionLabel"));
            RegisterName(prefix + "FolderButton", card.FindName("FolderButton"));
            RegisterName(prefix + "PollingIntervalInput", card.FindName("PollingInput"));
        }
        private void BrowseEpicClick(object sender, EventArgs args) { model.BrowseEpicFolder(); }
        private async void ConnectEpicClick(object sender, EventArgs args) { await model.ConnectEpicAsync(); }
        private async void RestartSteamClick(object sender, EventArgs args) { if (model.SteamCard?.Action == "Launch") await model.LaunchConfiguredLauncherAsync(false); else await model.RestartSteamAsync(); }
        private async void RestartEpicClick(object sender, EventArgs args) { if (model.EpicCard?.Action == "Launch") await model.LaunchConfiguredLauncherAsync(true); else await model.RestartEpicAsync(); }
        private async void RemoveSteamClick(object sender, EventArgs args) { await model.ConfirmRemoveConnectionAsync(false); }
        private async void RemoveEpicClick(object sender, EventArgs args) { await model.ConfirmRemoveConnectionAsync(true); }
        private void BrowseClick(object sender, EventArgs args) { model.BrowseSteamFolder(); }
        private async void EnableAccessClick(object sender, EventArgs args) { await model.EnableLocalAccessAsync(); }
    }
}
