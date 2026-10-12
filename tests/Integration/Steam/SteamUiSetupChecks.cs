using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting.Messaging;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;
using static UpdateStatus.Tests.Support.SteamSamples;

internal static partial class Program
{
    private static void SteamUiSetup()
    {
        string root = Path.Combine(Path.GetTempPath(), "SteamUiSetup-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(root, "steamclient64.dll"), "fixture; never load");
            Eq(false, SteamUiAccess.Configured(root));
            var setupView = BindSettingsView(new SettingsView(new PlayniteUpdateStatus.SettingsViewModel(new PlayniteUpdateStatus.Settings(), launcherSession: _ => "running"), () => root));
            var setupButton = (Button)setupView.FindName("SteamConnectButton");
            Eq(true, setupButton.IsEnabled);
            Eq("Set up connection", setupButton.Content);
            Eq(Visibility.Visible, setupButton.Visibility);
            Eq(false, ((Expander)setupView.FindName("SteamConnectionDetails")).IsExpanded);
            Eq(false, SteamUiAccess.Configured(root));
            setupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpFolderStatus(() => SteamUiAccess.Configured(root) && ((PlayniteUpdateStatus.SettingsViewModel)setupView.DataContext).ConnectionStatus == "Restart required");
            Eq(Visibility.Collapsed, setupButton.Visibility);
            Eq("Restart required", ((PlayniteUpdateStatus.SettingsViewModel)setupView.DataContext).ConnectionStatus);
            Eq(true, SteamUiAccess.Configured(root));
            string marker = Path.Combine(root, ".cef-enable-remote-debugging");
            File.WriteAllText(marker, "existing external marker");
            SteamUiAccess.Enable(root);
            Eq("existing external marker", File.ReadAllText(marker));
            File.SetAttributes(marker, FileAttributes.ReadOnly);
            try
            {
                SteamUiAccess.Enable(root);
                Eq(FileAttributes.ReadOnly, File.GetAttributes(marker) & FileAttributes.ReadOnly);
            }
            finally
            {
                File.SetAttributes(marker, FileAttributes.Normal);
            }

            File.Delete(marker);
            Directory.CreateDirectory(marker);
            File.WriteAllText(Path.Combine(marker, "preserved.txt"), "existing folder marker");
            Eq(true, SteamUiAccess.Configured(root));
            SteamUiAccess.Enable(root);
            Eq("existing folder marker", File.ReadAllText(Path.Combine(marker, "preserved.txt")));
            var settings = new PlayniteUpdateStatus.Settings();
            var configuredView = BindSettingsView(new SettingsView(new PlayniteUpdateStatus.SettingsViewModel(settings, launcherSession: _ => "running"), () => root));
            var configuredButton = (Button)configuredView.FindName("SteamConnectButton");
            Eq(false, configuredButton.IsEnabled);
            Eq("Set up connection", configuredButton.Content);
            Eq(Visibility.Collapsed, configuredButton.Visibility);
            File.Delete(Path.Combine(marker, "preserved.txt"));
            Directory.Delete(marker);
            SteamUiAccess.ObserveConnection(root);
            Eq(true, SteamUiAccess.Connected(root));
            SteamUiAccess.Enable(root);
            Eq(false, File.Exists(marker));
            Eq(false, Directory.Exists(marker));
            var connectedView = BindSettingsView(new SettingsView(new PlayniteUpdateStatus.SettingsViewModel(settings, launcherSession: _ => "running"), () => root));
            var connectedButton = (Button)connectedView.FindName("SteamConnectButton");
            Eq(false, connectedButton.IsEnabled);
            Eq("Set up connection", connectedButton.Content);
            Eq(Visibility.Collapsed, connectedButton.Visibility);
            Eq("Connected", ((PlayniteUpdateStatus.SettingsViewModel)connectedView.DataContext).ConnectionStatus);
            Eq(false, SteamUiAccess.Configured(Path.Combine(root, "missing")));
            // Exercise the actual helper's setup-only mode on an unrelated temporary
            // installation. No Steam connection, account, launch or queue access occurs.
            string helperRoot = Path.Combine(root, "Other PC Steam Ω");
            Directory.CreateDirectory(helperRoot);
            File.WriteAllText(Path.Combine(helperRoot, "steam.exe"), "fixture; never execute");
            File.WriteAllText(Path.Combine(helperRoot, "steamclient64.dll"), "fixture; never load");
            string helperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"SteamUpdateReader.exe");
            Func<string,int> runSetup = arguments => {
                using (var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helperPath,
                    SteamLocator.QuoteRoot(helperRoot) + " " + arguments) { UseShellExecute = false, CreateNoWindow = true }))
                { Eq(true,helper.WaitForExit(10000)); return helper.ExitCode; }
            };
            Eq(1,runSetup("--enable-local-access S-1-0-0"));
            Eq(false,SteamUiAccess.Configured(helperRoot));
            Eq(0,runSetup("--enable-local-access"));
            Eq(true,SteamUiAccess.Configured(helperRoot));
            Eq(0L,new FileInfo(Path.Combine(helperRoot,".cef-enable-remote-debugging")).Length);
            Eq(true,File.Exists(Path.Combine(helperRoot,".update-status-connection.json")));
            Eq(1,runSetup("--disable-local-access S-1-0-0"));
            Eq(true,SteamUiAccess.Configured(helperRoot));
            Eq(0,runSetup("--disable-local-access " + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value));
            Eq(false,SteamUiAccess.Configured(helperRoot));
            Eq(false,File.Exists(Path.Combine(helperRoot,".update-status-connection.json")));

        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
