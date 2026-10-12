using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.UI;

internal static partial class Program
{
    private static void SharedConnectionCardChecks()
    {
        Test("shared polling preserves custom values and updates saved settings without breaking bindings", () => {
            var settings = new Settings { EpicEnabled = true, PollingInterval = 1250, EpicPollingInterval = 1500 };
            var model = new SettingsViewModel(settings, () => null, findEpicFolder: _ => null, epicHasBackup: () => false);
            var view = BindSettingsView(new SettingsView(model));
            var steam = (LauncherConnectionCard)view.FindName("SteamCardControl");
            var polling = (ComboBox)view.FindName("SteamPollingIntervalInput");
            Eq(1250,settings.PollingInterval); Eq(4,polling.Items.Count); Eq(1250,polling.SelectedValue);
            polling.SelectedValue = 500;
            Eq(500,settings.PollingInterval);
            polling.SelectedValue = 2000;
            Eq(2000,settings.PollingInterval);
            Eq(true,System.Windows.Data.BindingOperations.IsDataBound(steam,LauncherConnectionCard.PollingIntervalProperty));
            Eq(false,((Expander)view.FindName("SteamConnectionDetails")).IsExpanded);
            Eq(1500,((ComboBox)view.FindName("EpicPollingIntervalInput")).SelectedValue);
            // The full SDK suite installs the host serializer; focused UI runs do not.
            if (typeof(Playnite.SDK.Data.Serialization).GetField("serializer",System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null) != null)
            {
                model.BeginEdit(); settings.PollingInterval = 1000; model.CancelEdit();
                view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
                Eq(2000,((ComboBox)view.FindName("SteamPollingIntervalInput")).SelectedValue);
            }
        });
        foreach (bool epic in new[] { false,true })
        {
            foreach (string state in new[] { "Not configured","Restart required","Connected","Not running","Connection error","Connecting…" })
            {
                string captureState = state;
                Test((epic ? "Epic" : "Steam") + " shared card presentation: " + state, () => {
                    var information = epic ? LauncherConnectionInformation.Epic : LauncherConnectionInformation.Steam;
                    var presentation = new LauncherConnectionCardState(information) {
                        Folder = @"C:\Program Files (x86)\" + information.InstallationName + @"\A long installation path Ω 中文 日本語 العربية Русский",
                        Enabled = true, ActionsAvailable = true, CanRemove = true
                    };
                    string action = state == "Not configured" ? "Setup" : state == "Not running" ? "Launch" :
                        state == "Restart required" || state == "Connection error" ? "Restart" : null;
                    presentation.Present(state,state == "Connected" ? "#78C8A0" : state == "Connection error" ? "#F18087" : "#E9BA66",
                        state == "Connected" ? "Supported update observation is active." : "Connection settings are saved. Use the available action to connect.", action);
                    var card = new LauncherConnectionCard { State = presentation, Width = 520, VerticalAlignment = VerticalAlignment.Top,
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16,23,43)),
                        Foreground = System.Windows.Media.Brushes.White };
                    card.Resources["ButtonBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22,35,57));
                    card.Resources["HighlightGlyphBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(90,200,245));
                    var window = new TestWindow { Content = card, Width = 570, Height = 800, Left = -32000, Top = -32000,
                        ShowActivated = false, ShowInTaskbar = false, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16,23,43)) };
                    try
                    {
                        window.Show(); window.UpdateLayout();
                        Eq(state,((TextBlock)card.FindName("StatusLabel")).Text);
                        Eq(action == "Setup" ? Visibility.Visible : Visibility.Collapsed,((Button)card.FindName("SetupButton")).Visibility);
                        Eq(action == "Launch" || action == "Restart" ? Visibility.Visible : Visibility.Collapsed,((Button)card.FindName("RelaunchButton")).Visibility);
                        Eq(false,((Expander)card.FindName("ConnectionDetails")).IsExpanded);
                        Render(card,"shared-connection-"+(epic ? "epic" : "steam")+"-"+captureState.Replace(" ","-").Replace("…","")+".png");
                        if (state == "Connected")
                        {
                            ((Expander)card.FindName("ConnectionDetails")).IsExpanded = true; window.UpdateLayout();
                            Render(card,"shared-connection-"+(epic ? "epic" : "steam")+"-advanced.png");
                        }
                    }
                    finally { window.Close(); }
                });
            }
        }
    }
}
