using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Playnite.SDK.Data;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Epic;
using PlayniteUpdateStatus.UI;

internal static partial class Program
{
    private static void EpicBetaChecks()
    {
        Test("beta settings separate common, Steam and Epic controls", () => {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null);
            var view = BindSettingsView(new SettingsView(model, () => null));
            var tabs = (TabControl)view.FindName("LauncherSettingsTabs");
            Eq("General,Steam,Epic", string.Join(",", tabs.Items.Cast<TabItem>().Select(item => item.Header)));
            Eq(true, ((StackPanel)view.FindName("GeneralSettingsPanel")).Children.OfType<CheckBox>().Any(c => Equals(c.Content,"Show update status")));
            Eq(view.FindName("SteamSettingsPanel"), ((ComboBox)view.FindName("ExitActionInput")).Parent);
            Eq(false, ((StackPanel)view.FindName("EpicSettingsPanel")).Children.OfType<ComboBox>().Any());
        });
        Test("Epic folder and connection presentation follows Steam without changing automatic preferences", () => {
            var settings = new Settings { EpicEnabled = true };
            var model = new SettingsViewModel(settings, () => null, findEpicFolder: _ => @"C:\Epic fixture",
                epicConfigured: _ => false, epicHasBackup: () => false, launcherSession: _ => "running");
            var snapshot = new EpicSnapshot(DateTime.MinValue, new EpicProduct[0], false, "Exit Epic, then use Connect to Epic");
            model.EpicConnection = () => snapshot;
            var view = new SettingsView(model, () => null);
            var discovery = model.RefreshEpicLocationAsync();
            PumpFolderStatus(() => discovery.IsCompleted);
            discovery.GetAwaiter().GetResult();
            BindSettingsView(view);
            var tabs = (TabControl)view.FindName("LauncherSettingsTabs");
            tabs.SelectedIndex = 2;
            view.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => {}));
            Eq(@"C:\Epic fixture", ((TextBlock)view.FindName("EpicFolderInput")).Text);
            Eq("", settings.EpicFolder);
            Eq("✓ Epic Games Launcher detected", ((TextBlock)view.FindName("EpicFolderStatus")).Text);
            Eq(true, ((TextBlock)view.FindName("EpicFolderHelp")).Text.Contains("Set up"));
            Eq("Not connected", model.EpicStatus);
            Eq(true, ((Button)view.FindName("EpicConnectButton")).IsEnabled);
            var steamPanel = (Border)view.FindName("SteamConnectionPanel");
            var epicPanel = (Border)view.FindName("EpicConnectionPanel");
            Eq(steamPanel.Width, epicPanel.Width); Eq(steamPanel.Margin, epicPanel.Margin);
            Eq(steamPanel.Padding, epicPanel.Padding); Eq(steamPanel.CornerRadius, epicPanel.CornerRadius);
            var steamPolling = (ComboBox)view.FindName("SteamPollingIntervalInput");
            Eq(steamPolling.MinWidth, ((ComboBox)view.FindName("EpicPollingIntervalInput")).MinWidth);
            snapshot = new EpicSnapshot(DateTime.UtcNow, new EpicProduct[0], true, "Connected — observation beta");
            model.RefreshEpicConnection();
            view.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => {}));
            Eq("Connected", model.EpicStatus);
            Eq(Visibility.Collapsed, ((Button)view.FindName("EpicConnectButton")).Visibility);
            Eq("Set up connection", ((Button)view.FindName("EpicConnectButton")).Content);
            Eq(false, ((Expander)view.FindName("EpicConnectionDetails")).IsExpanded);
        });
        Test("Epic settings survive SDK serialization and Cancel restores them", () => {
            var original = new Settings { EpicEnabled = false, EpicFolder = @"D:\Epic", EpicPollingInterval = 1500 };
            var saved = Serialization.FromJson<Settings>(Serialization.ToJson(original));
            Eq(false, saved.EpicEnabled); Eq(@"D:\Epic", saved.EpicFolder); Eq(1500, saved.EpicPollingInterval);
            var model = new SettingsViewModel(saved, () => null);
            model.BeginEdit(); saved.EpicEnabled = true; saved.EpicFolder = ""; saved.EpicPollingInterval = 500;
            model.CancelEdit();
            Eq(false, saved.EpicEnabled); Eq(@"D:\Epic", saved.EpicFolder); Eq(1500, saved.EpicPollingInterval);
        });
        Test("Epic connection label ignores stale readiness and is read-only", () => {
            var model = new SettingsViewModel(new Settings { EpicEnabled = true }, () => null, findEpicFolder: _ => @"C:\Epic fixture",
                epicConfigured: _ => false, epicHasBackup: () => false, launcherSession: _ => "running");
            var discovery = model.RefreshEpicLocationAsync();
            PumpFolderStatus(() => discovery.IsCompleted);
            discovery.GetAwaiter().GetResult();
            model.EpicConnection = () => new EpicSnapshot(DateTime.UtcNow.AddMinutes(-1), new EpicProduct[0], true, "Connected");
            model.RefreshEpicConnection();
            Eq(true, model.EpicCanConnect);
            Eq(false, model.EpicStatus.StartsWith("Connected"));
        });
        Test("Epic status renders hide-only Close without Pause or fake percentage", () => {
            using (var monitor = new UpdateMonitor((s,w) => {}))
            {
                var control = new StatusControl(monitor, true, true, true);
                var state = new UpdateState { SessionId = 1, GameId = Guid.NewGuid(), GameName = "Epic fixture",
                    LauncherName = "Epic Games", Phase = UpdatePhase.Updating,
                    Display = new UpdateDisplay { Stage = "Updating", WaitingForData = true } };
                control.Render(state);
                var panel = (StackPanel)((Border)control.Content).Child;
                var close = ((Grid)panel.Children[0]).Children.OfType<Button>().Single();
                Eq("Hide update status", AutomationProperties.GetName(close));
                Eq(false, panel.Children.OfType<Button>().Any(button => button.Visibility == Visibility.Visible));
                Eq(false, panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("%")));
                var window = new TestWindow { Content = control, Width = 550, Height = 300,
                    ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
                try { window.Show(); control.Render(state); window.UpdateLayout(); Render(control, "epic-observer-card.png"); }
                finally { window.Close(); }
            }
        });
        Test("Epic waiting state stays neutral, hides progress and recovers its card", () => {
            using (var monitor = new UpdateMonitor((s,w) => {}))
            {
                var control = new StatusControl(monitor, true, true, true);
                var state = new UpdateState { SessionId = 1, GameId = Guid.NewGuid(), GameName = "Epic fixture",
                    LauncherName = "Epic Games", Phase = UpdatePhase.WaitingForData, UserMessage = "Waiting for Epic update data…" };
                control.Render(state);
                var panel = (StackPanel)((Border)control.Content).Child;
                Eq(true, panel.Children.OfType<TextBlock>().Any(text => text.Text == state.UserMessage));
                Eq(false, panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("interrupted") || text.Text.Contains("%")));
                Eq(true, panel.Children.OfType<ProgressBar>().All(bar => bar.Visibility == Visibility.Collapsed));
                Eq(false, panel.Children.OfType<Button>().Any(button => button.Visibility == Visibility.Visible));
                var close = ((Grid)panel.Children[0]).Children.OfType<Button>().Single();
                Eq("Hide update status", AutomationProperties.GetName(close));
                state.Revision = 1;
                state.Phase = UpdatePhase.Updating;
                state.Display = new UpdateDisplay { Stage = "Updating", WaitingForData = true };
                control.Render(state);
                Eq(true, panel.Children.OfType<ProgressBar>().Any(bar => bar.Visibility == Visibility.Visible));
                Eq(true, panel.Children.OfType<TextBlock>().Any(text => text.Text == "Updating…"));
            }
        });
    }
}
