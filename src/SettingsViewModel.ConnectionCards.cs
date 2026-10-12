using System;
using System.Threading.Tasks;
using PlayniteUpdateStatus.UI;
using PlayniteUpdateStatus.Launchers.Epic;

namespace PlayniteUpdateStatus
{
    public sealed partial class SettingsViewModel
    {
        private LauncherConnectionCardState steamCard, epicCard;
        public LauncherConnectionCardState SteamCard { get { return steamCard; } private set { SetValue(ref steamCard, value, nameof(SteamCard)); } }
        public LauncherConnectionCardState EpicCard { get { return epicCard; } private set { SetValue(ref epicCard, value, nameof(EpicCard)); } }
        public bool EpicObservationEnabled
        {
            get { return Settings.EpicEnabled; }
            set { Settings.EpicEnabled = value; OnPropertyChanged(nameof(EpicObservationEnabled)); RefreshEpicConnection(); }
        }

        private void UpdateSteamCard(string session, bool pending, bool connected, bool configured)
        {
            SteamCard = CreateCard(false, session, pending, connected, configured && !steamRemoved);
            if (steamRemoved && configured && !SteamCard.Busy) SteamCard.Present("Observation disabled", "#8994A8",SteamRemovalNotice,"Setup");
        }
        private void UpdateEpicCard(string session, bool pending, bool connected)
        {
            EpicCard = CreateCard(true, session, pending, connected, epicSetupReady && !epicRemoved);
        }
        private LauncherConnectionCardState CreateCard(bool epic, string session, bool pending, bool connected, bool configured)
        {
            string root = epic ? resolvedEpicRoot : resolvedRoot;
            bool enabled = !epic || Settings.EpicEnabled;
            bool settingUp = epic ? epicConnecting : enablingAccess;
            bool restarting = epic ? restartingEpic : restartingSteam;
            bool launching = epic ? launchingEpic : launchingSteam;
            bool removing = epic ? removingEpic : removingSteam;
            string error = epic ? epicSetupError ?? EpicOperationMessage : accessError;
            DateTime reconnectUntil = epic ? epicReconnectUntil : steamReconnectUntil;
            var card = new LauncherConnectionCardState(epic ? LauncherConnectionInformation.Epic : LauncherConnectionInformation.Steam)
            {
                Folder = root ?? "", Enabled = enabled, ActionsAvailable = enabled && ConnectionActionsAvailable,
                CanRemove = epic ? EpicRemoveEnabled : SteamRemoveEnabled, Busy = settingUp || restarting || launching || removing
            };
            if (!enabled) card.Present("Disabled", "#8994A8", "Observation is disabled. Saved connection settings are kept.");
            else if (settingUp || restarting || launching || removing)
                card.Present(settingUp ? "Setting up…" : removing ? "Removing…" : restarting ? "Restarting…" : "Launching…", "#57BDEB",
                    "Please wait. Connection actions are temporarily unavailable.");
            else if (!string.IsNullOrEmpty(error))
                card.Present("Connection error", "#F18087", error, root == null ? null : configured ? session.Length == 0 ? "Launch" : "Restart" : "Setup");
            else if (root == null)
                card.Present("Launcher not found", "#E9BA66", "Choose the installation folder to continue.");
            else if (pending)
                card.Present("Restart required", "#E9BA66", "Connection settings changed. Restart the launcher to apply them.", "Restart");
            else if (connected)
                card.Present("Connected", "#78C8A0", epic ? "Supported update and download observation is active." : "Update progress and supported download controls are available.");
            else if (!configured)
                card.Present("Not configured", "#8994A8", "Set up the connection to enable update observation.", "Setup");
            else if (session.Length == 0)
                card.Present("Not running", "#8994A8", "Connection setup is saved. Launch the application to connect.", "Launch");
            else if (reconnectUntil > DateTime.UtcNow)
                card.Present("Connecting…", "#57BDEB", "Waiting for live communication with the launcher.");
            else
                card.Present("Restart required", "#E9BA66", epic ? "The launcher is running without a verified connection. Restart it through this plugin; some launch methods bypass setup." :
                    "The launcher is running without a verified connection. Restart it to reconnect.", "Restart");
            var snapshot = epic ? EpicConnection?.Invoke() : null;
            if (epic && Settings.EpicEnabled && !epicRemoved && root != null && session.Length != 0 && !pending &&
                !card.Busy && snapshot != null && snapshot.Failure != EpicConnectionFailure.None && string.IsNullOrEmpty(error))
            {
                string action = snapshot.Failure == EpicConnectionFailure.InterfaceUnavailable || snapshot.Failure == EpicConnectionFailure.InvalidResponse ? null :
                    configured ? "Restart" : "Setup";
                card.Present("Connection error","#F18087",EpicConnectionFailures.Message(snapshot.Failure),action);
            }
            return card;
        }

        public async Task ConfirmRemoveConnectionAsync(bool epic)
        {
            if (!(epic ? EpicRemoveEnabled : SteamRemoveEnabled)) return;
            if (!confirmRemoval(epic ? "Epic Games Launcher" : "Steam")) return;
            await (epic ? RemoveEpicConnectionAsync() : RemoveSteamConnectionAsync());
        }

        public async Task LaunchConfiguredLauncherAsync(bool epic)
        {
            var card = epic ? EpicCard : SteamCard;
            if (card == null || card.Action != "Launch" || !card.ActionsAvailable || launcherSession(epic).Length != 0) return;
            string root = epic ? resolvedEpicRoot : resolvedRoot;
            long revision = epic ? epicLocationRevision : locationRevision;
            connectionBusy = true; launchingEpic = epic; launchingSteam = !epic;
            if (epic) epicMessage = null; else accessError = null;
            RefreshConnection(); RefreshEpicConnection();
            try
            {
                await openAfterExit(root, epic);
                if (epic && revision == epicLocationRevision)
                { epicVerifyAfterUtc = DateTime.UtcNow; epicReconnectUntil = DateTime.UtcNow.AddSeconds(60); }
                if (!epic && revision == locationRevision) steamReconnectUntil = DateTime.UtcNow.AddSeconds(60);
            }
            catch (Exception error)
            {
                connectionLog((epic ? "Epic" : "Steam") + " launch failed: " + error.Message);
                if (epic && revision == epicLocationRevision) { epicMessage = error.Message; epicMessageUntil = DateTime.MaxValue; }
                if (!epic && revision == locationRevision) accessError = error.Message;
            }
            finally
            { connectionBusy = false; launchingEpic = launchingSteam = false; RefreshConnection(); RefreshEpicConnection(); }
        }
    }
}
