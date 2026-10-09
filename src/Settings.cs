using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus
{
    public sealed class Settings : ISettings
    {
        private readonly Plugin plugin;
        private Settings previous;
        public bool Enabled { get; set; }
        public string SteamFolder { get; set; }
        public int PollingInterval { get; set; }
        public bool KeepFrontendVisible { get; set; }
        public bool AutomaticOverlay { get; set; }
        public bool CoverBadges { get; set; }
        public SpeedUnit SpeedUnit { get; set; }
        public ExitDownloadAction ExitAction { get; set; }
        public Settings() { Enabled = KeepFrontendVisible = AutomaticOverlay = CoverBadges = true; PollingInterval = 1000; SteamFolder = ""; }
        public Settings(Plugin plugin) : this()
        {
            this.plugin = plugin;
            var loaded = plugin.LoadPluginSettings<Settings>();
            if (loaded != null) Copy(loaded);
        }
        private void Copy(Settings other)
        { Enabled = other.Enabled; SteamFolder = other.SteamFolder ?? ""; PollingInterval = other.PollingInterval; KeepFrontendVisible = other.KeepFrontendVisible; AutomaticOverlay = other.AutomaticOverlay; CoverBadges = other.CoverBadges; SpeedUnit = other.SpeedUnit; ExitAction = other.ExitAction; }
        public void BeginEdit() { previous = new Settings(); previous.Copy(this); }
        public void CancelEdit() { if (previous != null) Copy(previous); }
        public void EndEdit() { plugin.SavePluginSettings(this); plugin.ApplySettings(); }
        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (PollingInterval < 500 || PollingInterval > 2000) errors.Add("Polling interval must be between 500 and 2000 milliseconds.");
            if (!Enum.IsDefined(typeof(SpeedUnit),SpeedUnit)) errors.Add("Choose a valid download speed unit.");
            if (!Enum.IsDefined(typeof(ExitDownloadAction),ExitAction)) errors.Add("Choose a valid action for when Playnite closes.");
            if (!string.IsNullOrWhiteSpace(SteamFolder))
            {
                try {
                    string folder = Launchers.Steam.SteamLocator.NormalizeRoot(SteamFolder);
                    if (!Launchers.Steam.SteamLocator.IsValidRoot(SteamFolder.Trim())) errors.Add("Choose the Steam installation folder containing steam.exe and steamclient64.dll, or leave it blank for automatic detection.");
                    else SteamFolder = folder;
                } catch (ArgumentException) { errors.Add("The Steam folder path is invalid."); }
                catch (System.IO.IOException) { errors.Add("The Steam folder path is unavailable."); }
                catch (UnauthorizedAccessException) { errors.Add("The Steam folder path is inaccessible."); }
            }
            return errors.Count == 0;
        }
        public UserControl CreateView()
        { return CreateView(new Launchers.Steam.SteamLocator().FindAutomaticRoot); }
        public UserControl CreateView(Func<string> findAutomaticRoot)
        {
            var panel = new StackPanel { Margin = new Thickness(16), DataContext = this };
            AddCheck(panel, "Enable plugin", "Enabled");
            var folderStatus = new TextBlock { Name = "SteamFolderStatus", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,8) };
            var folderHelp = new TextBlock { Name = "SteamFolderHelp", Text = @"Select the Steam installation folder, e.g. C:\Program Files (x86)\Steam.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,4) };
            panel.Children.Add(folderHelp);
            var folderRow = new DockPanel { Width = 500, HorizontalAlignment = HorizontalAlignment.Left };
            folderRow.SetBinding(FrameworkElement.MaxWidthProperty,new Binding("ActualWidth") { Source = panel });
            var browse = new Button { Content = "Browse…", Margin = new Thickness(8,0,0,0), Padding = new Thickness(12,3,12,3) };
            DockPanel.SetDock(browse,Dock.Right); folderRow.Children.Add(browse);
            // A detected path is displayed without turning it into a manual
            // override. Only user editing/Browse changes the saved preference.
            if (!string.IsNullOrWhiteSpace(SteamFolder) && !Launchers.Steam.SteamLocator.IsValidRoot(SteamFolder.Trim())) SteamFolder = "";
            var folderInput = new TextBox { Name = "SteamFolderInput", Text = SteamFolder ?? "" };
            bool refreshingInput = false;
            folderRow.Children.Add(folderInput); panel.Children.Add(folderRow); panel.Children.Add(folderStatus);
            Action refreshLocation = () => {
                string selected = SteamFolder;
                bool manual = !string.IsNullOrWhiteSpace(selected);
                string found = null;
                if (manual) {
                    if (Launchers.Steam.SteamLocator.IsValidRoot(selected.Trim())) found = Launchers.Steam.SteamLocator.NormalizeRoot(selected);
                } else {
                    try { found = findAutomaticRoot(); }
                    catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
                    if (!Launchers.Steam.SteamLocator.IsValidRoot(found)) found = null;
                    else found = Launchers.Steam.SteamLocator.NormalizeRoot(found);
                    refreshingInput = true;
                    try { folderInput.Text = found ?? ""; }
                    finally { refreshingInput = false; }
                }
                folderStatus.Text = manual ? found != null ? "Manual Steam folder selected" : "Selected Steam folder is invalid" :
                    found != null ? "Steam found automatically" : "Steam not found automatically";
            };
            folderInput.TextChanged += (s,e) => {
                if (refreshingInput) return;
                SteamFolder = folderInput.Text;
                refreshLocation();
            };
            var locationTimer = new DispatcherTimer(DispatcherPriority.Background,panel.Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            locationTimer.Tick += (s,e) => refreshLocation();
            panel.Loaded += (s,e) => { refreshLocation(); locationTimer.Start(); };
            panel.Unloaded += (s,e) => locationTimer.Stop();
            refreshLocation();
            browse.Click += (s,e) => {
                if (plugin == null) return;
                string selected = plugin.SelectSteamFolder();
                if (string.IsNullOrEmpty(selected)) return;
                SteamFolder = selected;
                refreshingInput = true;
                try { folderInput.Text = selected; }
                finally { refreshingInput = false; }
                refreshLocation();
            };
            panel.Children.Add(new TextBlock { Text = "Polling interval (500–2000 ms)", Margin = new Thickness(0, 12, 0, 4) });
            var input = new TextBox { Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
            input.SetBinding(TextBox.TextProperty, new Binding("PollingInterval") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
            panel.Children.Add(input);
            panel.Children.Add(new TextBlock { Text = "Download speed units", Margin = new Thickness(0,12,0,4) });
            var speedUnits = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, SelectedValuePath = "Tag" };
            speedUnits.Items.Add(new ComboBoxItem { Content = "Bytes/s — KB/s, MB/s", Tag = SpeedUnit.BytesPerSecond });
            speedUnits.Items.Add(new ComboBoxItem { Content = "Bits/s — kbps, Mbps", Tag = SpeedUnit.BitsPerSecond });
            speedUnits.SetBinding(ComboBox.SelectedValueProperty,new Binding("SpeedUnit") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            panel.Children.Add(speedUnits);
            panel.Children.Add(new TextBlock { Text = "When Playnite closes", Margin = new Thickness(0,12,0,4) });
            var exitActions = new ComboBox { Name = "ExitActionInput", Width = 260, HorizontalAlignment = HorizontalAlignment.Left, SelectedValuePath = "Tag" };
            exitActions.Items.Add(new ComboBoxItem { Content = "Keep current state", Tag = ExitDownloadAction.KeepCurrentState });
            exitActions.Items.Add(new ComboBoxItem { Content = "Pause and keep in queue", Tag = ExitDownloadAction.PauseAndKeepInQueue });
            exitActions.Items.Add(new ComboBoxItem { Content = "Pause and remove from queue", Tag = ExitDownloadAction.PauseAndRemoveFromQueue });
            exitActions.SetBinding(ComboBox.SelectedValueProperty,new Binding("ExitAction") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            panel.Children.Add(exitActions);
            AddCheck(panel, "Show update status", "AutomaticOverlay");
            AddCheck(panel, "Show update indicators on covers and lists", "CoverBadges");
            AddCheck(panel, "Restore Playnite if Steam takes focus during an update", "KeepFrontendVisible");
            return new UserControl { Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        }
        private static CheckBox AddCheck(Panel panel, string label, string property)
        {
            var check = new CheckBox { Content = label, Margin = new Thickness(0, 8, 0, 0) };
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
            panel.Children.Add(check);
            return check;
        }
    }
}
