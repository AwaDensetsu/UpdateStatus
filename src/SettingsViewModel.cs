using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;

namespace PlayniteUpdateStatus
{
    public sealed class SettingsViewModel : ObservableObject, ISettings
    {
        private readonly Action<Settings> save;
        private readonly Action apply;
        private readonly Func<string> browse;
        private readonly Func<string, Task> enableAccess;
        private Func<string> findAutomaticRoot;
        private Settings previous;
        private string resolvedRoot, accessError;
        private long locationRevision;
        private string folderInput, folderStatus, connectionStatus, accessButtonText;
        private bool canEnableAccess, enablingAccess;

        public Settings Settings { get; }
        public string FolderInput
        {
            get { return folderInput; }
            set
            {
                if (folderInput == value) return;
                SetValue(ref folderInput, value);
                Settings.SteamFolder = value ?? "";
                RefreshSteamLocation();
            }
        }
        public string FolderStatus { get { return folderStatus; } private set { SetValue(ref folderStatus, value); } }
        public string ConnectionStatus { get { return connectionStatus; } private set { SetValue(ref connectionStatus, value); } }
        public string AccessButtonText { get { return accessButtonText; } private set { SetValue(ref accessButtonText, value); } }
        public bool CanEnableAccess { get { return canEnableAccess; } private set { SetValue(ref canEnableAccess, value); } }

        public SettingsViewModel(Settings settings, Func<string> findAutomaticRoot = null,
            Action<Settings> save = null, Action apply = null, Func<string> browse = null,
            Func<string, Task> enableAccess = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Settings.SteamFolder = Settings.SteamFolder ?? "";
            this.findAutomaticRoot = findAutomaticRoot ?? new SteamLocator().FindAutomaticRoot;
            this.save = save;
            this.apply = apply;
            this.browse = browse;
            this.enableAccess = enableAccess ?? SteamLocalAccessSetup.EnableAsync;
        }

        public void BeginEdit() { previous = Serialization.GetClone(Settings); }
        public void CancelEdit()
        {
            if (previous == null) return;
            Settings.Copy(previous);
            previous = null;
            OnPropertyChanged(nameof(Settings));
            RefreshSteamLocation();
        }
        public void EndEdit()
        {
            save?.Invoke(Settings);
            apply?.Invoke();
            previous = null;
        }
        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (Settings.PollingInterval < 500 || Settings.PollingInterval > 2000)
                errors.Add("Polling interval must be between 500 and 2000 milliseconds.");
            if (!Enum.IsDefined(typeof(SpeedUnit), Settings.SpeedUnit)) errors.Add("Choose a valid download speed unit.");
            if (!Enum.IsDefined(typeof(ExitDownloadAction), Settings.ExitAction)) errors.Add("Choose a valid action for when Playnite closes.");
            if (!string.IsNullOrWhiteSpace(Settings.SteamFolder))
            {
                try
                {
                    if (!SteamLocator.IsValidRoot(Settings.SteamFolder.Trim()))
                        errors.Add("Choose the Steam installation folder containing steam.exe and steamclient64.dll, or leave it blank for automatic detection.");
                    else Settings.SteamFolder = SteamLocator.NormalizeRoot(Settings.SteamFolder);
                }
                catch (ArgumentException) { errors.Add("The Steam folder path is invalid."); }
                catch (IOException) { errors.Add("The Steam folder path is unavailable."); }
                catch (UnauthorizedAccessException) { errors.Add("The Steam folder path is inaccessible."); }
            }
            return errors.Count == 0;
        }

        // Invoked when a settings view opens, not while loading preferences.
        public void InitializeLocation(Func<string> discovery = null)
        {
            if (discovery != null) findAutomaticRoot = discovery;
            if (!string.IsNullOrWhiteSpace(Settings.SteamFolder) && !SteamLocator.IsValidRoot(Settings.SteamFolder.Trim()))
                Settings.SteamFolder = "";
            RefreshSteamLocation();
        }

        public void RefreshSteamLocation()
        {
            bool manual = !string.IsNullOrWhiteSpace(Settings.SteamFolder);
            string found = null;
            try
            {
                string candidate = manual ? Settings.SteamFolder.Trim() : findAutomaticRoot();
                if (SteamLocator.IsValidRoot(candidate)) found = SteamLocator.NormalizeRoot(candidate);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (System.Security.SecurityException) { }
            if (!string.Equals(resolvedRoot, found, StringComparison.OrdinalIgnoreCase))
            {
                locationRevision++;
                accessError = null;
            }
            resolvedRoot = found;
            // A detected path is displayed without becoming a manual override.
            SetValue(ref folderInput, manual ? Settings.SteamFolder : found ?? "", nameof(FolderInput));
            FolderStatus = manual ? found != null ? "Manual Steam folder selected" : "Selected Steam folder is invalid" :
                found != null ? "Steam found automatically" : "Steam not found automatically";
            RefreshConnection();
        }

        public void BrowseSteamFolder()
        {
            string selected = browse?.Invoke();
            if (string.IsNullOrEmpty(selected)) return;
            // Browse explicitly selects a manual folder even if it matches the detected path.
            Settings.SteamFolder = selected;
            RefreshSteamLocation();
        }

        public async Task EnableLocalAccessAsync()
        {
            if (!CanEnableAccess || enablingAccess) return;
            string root = resolvedRoot;
            long revision = locationRevision;
            enablingAccess = true;
            accessError = null;
            RefreshConnection();
            try { await enableAccess(root); }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
            { if (revision == locationRevision) accessError = "Local access was not enabled — administrator permission was cancelled."; }
            catch (Exception error) { if (revision == locationRevision) accessError = "Could not enable local access: " + error.Message; }
            finally
            {
                enablingAccess = false;
                RefreshConnection();
            }
        }

        private void RefreshConnection()
        {
            bool valid = SteamLocator.IsValidRoot(resolvedRoot);
            bool configured = valid && SteamUiAccess.Configured(resolvedRoot);
            bool connected = valid && SteamUiAccess.Connected(resolvedRoot);
            if (configured || connected) accessError = null;
            ConnectionStatus = enablingAccess ? "Setting up the connection…" : accessError ??
                (connected ? "Connected" : configured ?
                    "Open or restart Steam to connect" : valid ? "Not connected" : "Select the Steam folder to connect");
            CanEnableAccess = valid && !configured && !connected && !enablingAccess;
            AccessButtonText = enablingAccess ? "Connecting…" : connected ? "Connected" :
                configured ? "Connection enabled" : "Connect to Steam";
        }
    }
}
