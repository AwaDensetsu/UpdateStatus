using System;
using System.Windows;
using System.Windows.Controls;
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
            model.InitializeLocation(discovery);
            DataContext = model;
            refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromSeconds(2) };
            refreshTimer.Tick += (s, e) => model.RefreshSteamLocation();
            Loaded += (s, e) => { model.RefreshSteamLocation(); refreshTimer.Start(); };
            Unloaded += (s, e) => refreshTimer.Stop();
        }

        private void BrowseClick(object sender, RoutedEventArgs args) { model.BrowseSteamFolder(); }
        private async void EnableAccessClick(object sender, RoutedEventArgs args) { await model.EnableLocalAccessAsync(); }
    }
}
