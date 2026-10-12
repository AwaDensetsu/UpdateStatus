using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.Launchers.Epic;

namespace PlayniteUpdateStatus
{
    public sealed partial class SettingsViewModel : ObservableObject, ISettings
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
        private bool canEnableAccess, enablingAccess, connectionBusy, restartingSteam, restartingEpic;
        private readonly Func<string, Task> disableAccess, restartSteam, restartEpic;
        private readonly Func<string, bool, Task> openAfterExit;
        private readonly Func<string, bool, Task> configureEpic;
        private readonly Func<string, bool> epicConfiguredQuery, epicSetupReadyQuery;
        private readonly Func<bool> epicBackupQuery, steamInformationQuery;
        private readonly Func<bool, string> launcherSession;
        private readonly Func<string, bool> confirmRestart, confirmManualExit, confirmRemoval;
        private readonly Action<string> connectionLog;
        private string steamRestartSession, epicRestartSession;
        private DateTime steamReconnectUntil, epicReconnectUntil, epicVerifyAfterUtc;
        private bool launchingSteam, launchingEpic, removingSteam, removingEpic;
        private bool epicBackup, epicSetupReady;
        private bool steamRemoved => Settings.SteamConnectionRemoved;
        private bool epicRemoved => Settings.EpicConnectionRemoved;
        private string epicSetupError;
        private bool ConnectionActionsAvailable => !connectionBusy && !enablingAccess && !epicConnecting;
        public bool SteamCanRestart => SteamLocator.IsValidRoot(resolvedRoot) && ConnectionActionsAvailable;
        public bool SteamCanRemove => SteamCanRestart && !steamRemoved &&
            (SteamUiAccess.Configured(resolvedRoot) || SteamUiAccess.Connected(resolvedRoot) || steamInformationQuery());
        public bool EpicCanRestart => Settings.EpicEnabled && resolvedEpicRoot != null && ConnectionActionsAvailable;
        public bool EpicCanRemove => ConnectionActionsAvailable && !epicRemoved &&
            (epicBackup || EpicConnection?.Invoke()?.Fresh == true);
        public bool SteamRemoveEnabled => SteamCanRemove;
        public bool EpicRemoveEnabled => Settings.EpicEnabled && EpicCanRemove;
        public string SteamRemovalNotice => steamRemoved ? SteamUiAccess.Configured(resolvedRoot) ? "Observation disabled. Pre-existing or changed Steam setup was preserved." : "Connection removed." : null;
        public string EpicRemovalNotice => epicRemoved ? "Connection removed." : null;
        public bool SteamRestartRequired => ConnectionStatus?.StartsWith("Restart required", StringComparison.Ordinal) == true;
        public bool EpicRestartRequired => EpicStatus == "Restart required";


        public Func<EpicSnapshot> EpicConnection { get; set; }
        private readonly Func<string, string> findEpicFolder;
        private string epicStatus = "Not connected", epicMessage, epicFolderInput = "", resolvedEpicRoot;
        private string epicFolderStatus = "Epic not found automatically";
        private DateTime epicMessageUntil;
        private bool epicConnecting;
        private Task epicLocationRefresh;
        private long epicLocationRevision;
        public string EpicOperationMessage => epicMessageUntil > DateTime.UtcNow ? epicMessage : null;
        public string EpicStatus { get { return epicStatus; } private set { SetValue(ref epicStatus, value); } }
        public string EpicFolderStatus { get { return epicFolderStatus; } private set { SetValue(ref epicFolderStatus, value); } }
        public string EpicFolderInput
        {
            get { return epicFolderInput; }
            set
            {
                if (epicFolderInput == value) return;
                SetValue(ref epicFolderInput, value);
                Settings.EpicFolder = value ?? "";
                InvalidateEpicLocation();
                _ = RefreshEpicLocationAsync();
            }
        }
        public bool EpicShowSetup => (epicRemoved || !epicSetupReady) && resolvedEpicRoot != null;
        public bool EpicCanConnect => Settings.EpicEnabled && EpicShowSetup && ConnectionActionsAvailable;
        public string EpicAccessButtonText => epicConnecting ? "Setting up…" : "Set up connection";

        private void InvalidateEpicLocation()
        {
            epicLocationRevision++;
            resolvedEpicRoot = null;
            epicSetupReady = false;
            epicReconnectUntil = DateTime.MinValue;
            epicMessage = null;
            epicMessageUntil = DateTime.MinValue;
            RefreshEpicConnection();
        }
        public Task RefreshEpicLocationAsync()
        {
            if (epicLocationRefresh == null || epicLocationRefresh.IsCompleted)
                epicLocationRefresh = ReadEpicLocationAsync();
            return epicLocationRefresh;
        }
        private async Task ReadEpicLocationAsync()
        {
            while (true)
            {
                long revision = epicLocationRevision;
                string input = Settings.EpicFolder ?? "";
                bool manual = !string.IsNullOrWhiteSpace(input);
                string found = null, setupError = null;
                bool configured = false, backup = false, setupReady = false;
                try
                {
                    await Task.Run(() => {
                        found = findEpicFolder(manual ? input.Trim() : null);
                        backup = epicBackupQuery();
                        if (found != null)
                        {
                            configured = epicConfiguredQuery(found);
                            setupReady = configured || backup && epicSetupReadyQuery(found);
                        }
                    });
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                    error is ArgumentException || error is System.Security.SecurityException ||
                    error is System.Reflection.TargetInvocationException || error is System.Runtime.InteropServices.COMException)
                { setupError = "Check the Epic connection setup"; }
                // An edit or Cancel during discovery must not restore the old path.
                if (revision != epicLocationRevision || input != (Settings.EpicFolder ?? "")) continue;
                resolvedEpicRoot = found;
                epicSetupReady = setupReady;
                epicBackup = backup;
                epicSetupError = setupError;
                SetValue(ref epicFolderInput, manual ? input : found ?? "", nameof(EpicFolderInput));
                EpicFolderStatus = manual ? found != null ? "Manual Epic folder selected" : "Selected Epic folder is invalid" :
                    found != null ? "Epic found automatically" : "Epic not found automatically";
                RefreshEpicConnection();
                return;
            }
        }
        public void RefreshEpicConnection()
        {
            string session = launcherSession(true);
            bool pending = RestartPending(ref epicRestartSession, session);
            var snapshot = EpicConnection?.Invoke();
            bool connected = Settings.EpicEnabled && !epicRemoved && session.Length != 0 &&
                snapshot?.Fresh == true && snapshot.ReceivedUtc >= epicVerifyAfterUtc;
            EpicStatus = launchingEpic ? "Launching Epic…" : restartingEpic ? "Restarting Epic…" : epicConnecting ? "Setting up the connection…" : pending ? "Restart required" :
                epicSetupError ?? (resolvedEpicRoot == null ? "Select the Epic folder to connect" :
                !Settings.EpicEnabled ? "Epic monitoring disabled" : connected ? "Connected" :
                !epicRemoved && epicSetupReady ? session.Length == 0 ? "Not running" : epicReconnectUntil > DateTime.UtcNow ? "Connecting…" : "Restart required" : "Not connected");
            OnPropertyChanged(nameof(EpicShowSetup));
            OnPropertyChanged(nameof(EpicCanConnect));
            OnPropertyChanged(nameof(EpicAccessButtonText));
            OnPropertyChanged(nameof(EpicCanRemove));
            OnPropertyChanged(nameof(EpicRemoveEnabled));
            OnPropertyChanged(nameof(EpicCanRestart));
            OnPropertyChanged(nameof(EpicRestartRequired));
            OnPropertyChanged(nameof(EpicOperationMessage));
            OnPropertyChanged(nameof(EpicRemovalNotice));
            UpdateEpicCard(session, pending, connected);
        }
        public void BrowseEpicFolder()
        {
            if (!ConnectionActionsAvailable || !Settings.EpicEnabled) return;
            var folder = browse?.Invoke();
            if (string.IsNullOrWhiteSpace(folder)) return;
            if (!ConnectionActionsAvailable) return;
            try
            {
                if (findEpicFolder(folder) == null)
                { epicMessage = "Choose the Epic installation folder containing EpicGamesLauncher.exe."; epicMessageUntil = DateTime.MaxValue; RefreshEpicConnection(); return; }
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
            { epicMessage = "The selected Epic folder is unavailable: " + error.Message; epicMessageUntil = DateTime.MaxValue; RefreshEpicConnection(); return; }
            // Explicit selection does not change saved automatic detection until validated.
            Settings.EpicFolder = folder;
            InvalidateEpicLocation();
            _ = RefreshEpicLocationAsync();
        }
        public async Task ConnectEpicAsync()
        {
            if (!EpicCanConnect) return;
            epicConnecting = true;
            await ChangeEpicConnectionAsync(false);
            epicConnecting = false;
            RefreshConnection(); RefreshEpicConnection();
        }
        public async Task RemoveEpicConnectionAsync()
        {
            if (!EpicCanRemove) return;
            await ChangeEpicConnectionAsync(true);
        }
        private async Task ChangeEpicConnectionAsync(bool remove)
        {
            if (connectionBusy || enablingAccess) return;
            connectionBusy = true;
            removingEpic = remove;
            string folder = resolvedEpicRoot ?? Settings.EpicFolder;
            long revision = epicLocationRevision;
            string action = remove ? "removal" : "setup";
            connectionLog("Epic connection " + action + " requested");
            RefreshConnection(); RefreshEpicConnection();
            try
            {
                await configureEpic(folder, remove);
                if (revision == epicLocationRevision)
                {
                    CommitConnectionChange(true, remove);
                    epicRestartSession = launcherSession(true);
                    epicMessage = null;
                }
                connectionLog("Epic connection " + action + " completed");
            }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                connectionLog("Epic connection " + action + " cancelled");
                if (revision == epicLocationRevision) epicMessage = "Connection change cancelled.";
            }
            catch (Exception error)
            {
                connectionLog("Epic connection " + action + " failed: " + error.GetType().Name + ": " + error.Message);
                if (revision == epicLocationRevision) epicMessage = error.Message;
            }
            finally
            {
                connectionBusy = false;
                removingEpic = false;
                if (revision == epicLocationRevision) { epicLocationRevision++; revision = epicLocationRevision; }
                if (revision == epicLocationRevision) epicMessageUntil = DateTime.UtcNow.AddSeconds(30);
                await RefreshEpicLocationAsync();
                RefreshConnection(); RefreshEpicConnection();
            }
        }
        public async Task RemoveSteamConnectionAsync()
        {
            if (!SteamCanRemove) return;
            string root = resolvedRoot;
            long revision = locationRevision;
            connectionBusy = true; removingSteam = true; accessError = null;
            RefreshConnection(); RefreshEpicConnection();
            try
            {
                await disableAccess(root);
                if (revision == locationRevision)
                {
                    CommitConnectionChange(false, true);
                    steamRestartSession = SteamUiAccess.Configured(root) ? null : launcherSession(false);
                }
            }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
            { if (revision == locationRevision) accessError = "Connection change cancelled."; }
            catch (Exception error)
            { if (revision == locationRevision) accessError = "Could not remove the connection: " + error.Message; }
            finally { connectionBusy = false; removingSteam = false; RefreshConnection(); RefreshEpicConnection(); }
        }
        public Task RestartSteamAsync() => RestartAsync(false);
        public Task RestartEpicAsync() => RestartAsync(true);
        private async Task RestartAsync(bool epic)
        {
            string launcher = epic ? "Epic" : "Steam";
            if (!(epic ? EpicCanRestart : SteamCanRestart))
            { connectionLog(launcher + " restart unavailable or busy"); return; }
            connectionLog(launcher + " restart confirmation requested");
            if (!confirmRestart(epic ? "Epic Games Launcher" : "Steam"))
            { connectionLog(launcher + " restart cancelled"); return; }
            connectionLog(launcher + " restart accepted");
            string root = epic ? resolvedEpicRoot : resolvedRoot;
            long revision = epic ? epicLocationRevision : locationRevision;
            connectionBusy = true;
            restartingEpic = epic; restartingSteam = !epic;
            if (epic) epicMessage = null; else accessError = null;
            RefreshConnection(); RefreshEpicConnection();
            try
            {
                try { await (epic ? restartEpic(root) : restartSteam(root)); }
                catch (LauncherStillRunningException) when (!epic)
                {
                    connectionLog(launcher + " normal shutdown did not finish; requesting manual Exit");
                    if (revision != locationRevision || !confirmManualExit("Steam"))
                        throw new IOException("Restart required — exit Steam completely from the system tray, then use Restart again.");
                    await openAfterExit(root, false);
                }
                connectionLog(launcher + " reopen confirmed by process identity");
                if (epic && revision == epicLocationRevision)
                { epicRestartSession = null; epicVerifyAfterUtc = DateTime.UtcNow; epicReconnectUntil = DateTime.UtcNow.AddSeconds(60); }
                if (!epic && revision == locationRevision)
                { steamRestartSession = null; steamReconnectUntil = DateTime.UtcNow.AddSeconds(60); }
            }
            catch (Exception error)
            {
                if (epic && revision == epicLocationRevision)
                { epicMessage = error.Message; epicMessageUntil = DateTime.MaxValue; }
                connectionLog(launcher + " restart failed: " + error.GetType().Name + ": " + error.Message);
                if (!epic && revision == locationRevision) accessError = error.Message.StartsWith("Restart required", StringComparison.Ordinal) ? error.Message : "Restart required — " + error.Message;
            }
            finally { connectionBusy = false; restartingEpic = restartingSteam = false; RefreshConnection(); RefreshEpicConnection(); }
        }
        private static bool RestartPending(ref string pending, string current)
        {
            if (string.IsNullOrEmpty(pending)) return false;
            if (pending == current) return true;
            pending = null;
            return false;
        }
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
            Func<string, Task> enableAccess = null, Func<string, string> findEpicFolder = null,
            Func<string, bool> epicConfigured = null, Func<bool> epicHasBackup = null,
            Func<string, bool, Task> configureEpic = null, Func<string, Task> disableAccess = null,
            Func<string, Task> restartSteam = null, Func<string, Task> restartEpic = null,
            Func<bool, string> launcherSession = null, Func<string, bool> confirmRestart = null,
            Func<string, bool> confirmManualExit = null, Func<string, bool, Task> openAfterExit = null,
            Func<bool> steamHasInformation = null, Action<string> connectionLog = null,
            Func<string, bool> epicSetupReady = null, Func<string, bool> confirmRemoval = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Settings.SteamFolder = Settings.SteamFolder ?? "";
            Settings.EpicFolder = Settings.EpicFolder ?? "";
            this.findEpicFolder = findEpicFolder ?? EpicLocalAccess.FindInstallationFolder;
            this.findAutomaticRoot = findAutomaticRoot ?? new SteamLocator().FindAutomaticRoot;
            this.save = save;
            this.apply = apply;
            this.browse = browse;
            this.enableAccess = enableAccess ?? SteamLocalAccessSetup.EnableAsync;
            this.disableAccess = disableAccess ?? SteamLocalAccessSetup.DisableAsync;
            this.restartSteam = restartSteam ?? LauncherConnectionActions.RestartSteamAsync;
            this.restartEpic = restartEpic ?? LauncherConnectionActions.RestartEpicAsync;
            this.launcherSession = launcherSession ?? LauncherConnectionActions.Session;
            this.confirmRestart = confirmRestart ?? (_ => false);
            this.confirmManualExit = confirmManualExit ?? (_ => false);
            this.confirmRemoval = confirmRemoval ?? (_ => false);
            this.openAfterExit = openAfterExit ?? LauncherConnectionActions.OpenAfterExitAsync;
            steamInformationQuery = steamHasInformation ?? (() => false);
            this.connectionLog = connectionLog ?? (_ => {});
            var setup = new EpicConnectionSetup();
            this.epicConfiguredQuery = epicConfigured ?? setup.Configured;
            this.epicSetupReadyQuery = epicSetupReady ?? epicConfigured ??
                (folder => setup.GetRestartArguments(folder).Length != 0);
            this.epicBackupQuery = epicHasBackup ?? (() => setup.HasBackup);
            this.configureEpic = configureEpic ?? LauncherConnectionActions.SetUpEpicAsync;
        }

        // Setup/removal changes the launcher immediately and must survive settings Cancel.
        private void CommitConnectionChange(bool epic, bool removed)
        {
            if (epic) Settings.EpicConnectionRemoved = removed;
            else Settings.SteamConnectionRemoved = removed;
            if (previous != null)
            {
                if (epic) previous.EpicConnectionRemoved = removed;
                else previous.SteamConnectionRemoved = removed;
            }
            try { save?.Invoke(previous ?? Settings); }
            finally { apply?.Invoke(); }
        }
        public void BeginEdit() { previous = Serialization.GetClone(Settings); }
        public void CancelEdit()
        {
            if (previous == null) return;
            Settings.Copy(previous);
            previous = null;
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(EpicObservationEnabled));
            RefreshSteamLocation();
            InvalidateEpicLocation();
            _ = RefreshEpicLocationAsync();
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
            if (Settings.EpicPollingInterval < 500 || Settings.EpicPollingInterval > 2000)
                errors.Add("Epic polling interval must be between 500 and 2000 milliseconds.");
            if (!string.IsNullOrWhiteSpace(Settings.EpicFolder))
            {
                try { if (EpicLocalAccess.FindExecutable(Settings.EpicFolder) == null) errors.Add("Choose a valid Epic installation folder, or leave it blank for automatic detection."); }
                catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException)
                { errors.Add("The Epic installation folder is unavailable."); }
            }
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
                steamRestartSession = null;
                steamReconnectUntil = DateTime.MinValue;
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
            if (!ConnectionActionsAvailable) return;
            string selected = browse?.Invoke();
            if (string.IsNullOrEmpty(selected)) return;
            if (!ConnectionActionsAvailable) return;
            if (!SteamLocator.IsValidRoot(selected))
            { accessError = "Choose the Steam installation folder containing steam.exe and steamclient64.dll."; RefreshConnection(); return; }
            accessError = null;
            Settings.SteamFolder = selected;
            RefreshSteamLocation();
        }

        public async Task EnableLocalAccessAsync()
        {
            if (!CanEnableAccess || enablingAccess || connectionBusy) return;
            string root = resolvedRoot;
            long revision = locationRevision;
            bool configurationChanged = !SteamUiAccess.Configured(root);
            enablingAccess = true;
            accessError = null;
            RefreshConnection(); RefreshEpicConnection();
            try
            {
                await enableAccess(root);
                if (revision == locationRevision)
                { CommitConnectionChange(false, false); steamRestartSession = configurationChanged ? launcherSession(false) : null; }
            }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
            { if (revision == locationRevision) accessError = "Local access was not enabled — administrator permission was cancelled."; }
            catch (Exception error) { if (revision == locationRevision) accessError = "Could not enable local access: " + error.Message; }
            finally
            {
                enablingAccess = false;
                RefreshConnection(); RefreshEpicConnection();
            }
        }

        private void RefreshConnection()
        {
            bool valid = SteamLocator.IsValidRoot(resolvedRoot);
            bool configured = valid && SteamUiAccess.Configured(resolvedRoot);
            string session = launcherSession(false);
            bool connected = valid && !steamRemoved && session.Length != 0 && SteamUiAccess.Connected(resolvedRoot);
            bool pending = RestartPending(ref steamRestartSession, session);
            ConnectionStatus = launchingSteam ? "Launching Steam…" : restartingSteam ? "Restarting Steam…" : enablingAccess ? "Setting up the connection…" : accessError ??
                (pending ? "Restart required" : connected ? "Connected" : !steamRemoved && configured ?
                    session.Length == 0 ? "Not running" : steamReconnectUntil > DateTime.UtcNow ? "Connecting…" : "Restart required" : valid ? "Not connected" : "Select the Steam folder to connect");
            CanEnableAccess = valid && (steamRemoved || !configured && !connected) && ConnectionActionsAvailable;
            AccessButtonText = enablingAccess ? "Setting up…" : "Set up connection";
            OnPropertyChanged(nameof(SteamCanRestart));
            OnPropertyChanged(nameof(SteamCanRemove));
            OnPropertyChanged(nameof(SteamRemoveEnabled));
            OnPropertyChanged(nameof(SteamRestartRequired));
            OnPropertyChanged(nameof(SteamRemovalNotice));
            UpdateSteamCard(session, pending, connected, configured);
        }
    }
}
