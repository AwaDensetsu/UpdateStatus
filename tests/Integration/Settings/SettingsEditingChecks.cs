using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK.Data;
using PlayniteUpdateStatus;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.UI;

internal static partial class Program
{
    // XAML bindings attach on the dispatcher, including for detached test views.
    private static UserControl BindSettingsView(UserControl view)
    {
        var tabs = view.FindName("LauncherSettingsTabs") as TabControl;
        if (tabs != null)
        {
            // Instantiate both shared cards before inspecting controls on inactive tabs.
            tabs.SelectedIndex = 2;
            view.Dispatcher.Invoke(DispatcherPriority.DataBind,new Action(() => {}));
            tabs.SelectedIndex = 1;
        }
        view.Measure(new Size(640,700));
        view.Arrange(new Rect(0,0,640,700));
        view.UpdateLayout();
        view.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        return view;
    }

    private static void SdkSettingsEditing()
    {
        Test("saved settings retain old defaults and preference names", () => {
            var settings = Serialization.FromJson<Settings>("{\"CoverBadges\":false}");
            Eq(true, settings.Enabled && settings.AutomaticOverlay && settings.KeepFrontendVisible);
            Eq(false, settings.CoverBadges);
            Eq(false, settings.EpicEnabled);
            Eq(false, settings.SteamConnectionRemoved || settings.EpicConnectionRemoved);
            Eq(1000, settings.PollingInterval);
            Eq("", settings.SteamFolder);
            Eq(SpeedUnit.BytesPerSecond, settings.SpeedUnit);
            Eq(ExitDownloadAction.KeepCurrentState, settings.ExitAction);
            var nullFolder = new SettingsViewModel(Serialization.FromJson<Settings>("{\"SteamFolder\":null}"));
            Eq("", nullFolder.Settings.SteamFolder);
            var names = Serialization.FromJson<Dictionary<string, object>>(Serialization.ToJson(settings)).Keys.OrderBy(value => value);
            Eq(string.Join(",", new[] { "Enabled", "SteamFolder", "PollingInterval", "KeepFrontendVisible",
                "AutomaticOverlay", "CoverBadges", "SpeedUnit", "ExitAction", "EpicEnabled", "EpicFolder", "EpicPollingInterval", "SteamConnectionRemoved", "EpicConnectionRemoved" }.OrderBy(value => value)), string.Join(",", names));
        });
        foreach (bool enabled in new[] { false, true })
        {
            Test("saved Epic choice survives upgrade and cancelled edits: " + enabled, () => {
                var saved = Serialization.FromJson<Settings>("{\"EpicEnabled\":" + (enabled ? "true" : "false") + "}");
                Eq(enabled, saved.EpicEnabled);
                var roundTrip = Serialization.FromJson<Settings>(Serialization.ToJson(saved));
                Eq(enabled, roundTrip.EpicEnabled);
                var model = new SettingsViewModel(roundTrip, () => null);
                Eq(enabled, model.EpicObservationEnabled);
                model.BeginEdit();
                model.EpicObservationEnabled = !enabled;
                model.CancelEdit();
                Eq(enabled, roundTrip.EpicEnabled);
            });
        }
        Test("settings Cancel preserves model identity and notifies bindings", () => {
            var settings = new Settings { EpicEnabled = true };
            int saves = 0;
            var model = new SettingsViewModel(settings, () => null, _ => saves++);
            var changed = new List<string>();
            model.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            model.BeginEdit();
            settings.CoverBadges = false;
            settings.PollingInterval = 500;
            settings.EpicEnabled = false;
            settings.EpicFolder = "temporary";
            settings.EpicPollingInterval = 2000;
            settings.ExitAction = ExitDownloadAction.PauseAndRemoveFromQueue;
            model.CancelEdit();
            Eq(true, ReferenceEquals(model.Settings, settings));
            Eq(true, settings.CoverBadges);
            Eq(1000, settings.PollingInterval);
            Eq(true, settings.EpicEnabled);
            Eq("", settings.EpicFolder);
            Eq(1000, settings.EpicPollingInterval);
            Eq(ExitDownloadAction.KeepCurrentState, settings.ExitAction);
            Eq(true, changed.Contains("Settings"));
            Eq(0, saves);
        });
        Test("settings Save persists before applying and ends edit snapshot", () => {
            var calls = new List<string>();
            var settings = new Settings();
            var model = new SettingsViewModel(settings, save: saved => {
                Eq(true, ReferenceEquals(saved, settings));
                Eq(false, saved.CoverBadges);
                calls.Add("save");
            }, apply: () => calls.Add("apply"));
            model.BeginEdit();
            settings.CoverBadges = false;
            model.EndEdit();
            model.CancelEdit();
            Eq(false, settings.CoverBadges);
            Eq("save,apply", string.Join(",", calls));
        });
    }
}
