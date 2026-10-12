namespace PlayniteUpdateStatus.UI
{
    // Presentation only. Each provider continues to own its detection, setup and cleanup.
    public sealed class LauncherConnectionCardState
    {
        public LauncherConnectionInformation Information { get; }
        public LauncherConnectionCardState(LauncherConnectionInformation information) { Information = information; }
        public string Folder { get; set; }
        public bool Detected => !string.IsNullOrEmpty(Folder);
        public string DetectionText => Detected ? "✓ " + Information.InstallationName + " detected" : Information.InstallationName + " not found";
        public string FolderActionText => Detected ? "Change" : "Browse…";
        public bool Enabled { get; set; }
        public bool ActionsAvailable { get; set; }
        public bool CanRemove { get; set; }
        public bool Busy { get; set; }
        public string Status { get; private set; }
        public string StatusColor { get; private set; }
        public string Description { get; private set; }
        public string Action { get; private set; }
        public bool CanSetup => ActionsAvailable && ShowSetup;
        public bool CanRelaunch => ActionsAvailable && ShowRelaunch;
        public bool ShowSetup => Action == "Setup";
        public bool ShowRelaunch => Action == "Launch" || Action == "Restart";
        public string RelaunchText => (Action == "Launch" ? "Launch " : "Restart ") + Information.ShortName;
        public void Present(string status, string color, string description, string action = null)
        { Status = status; StatusColor = color; Description = description; Action = action; }
    }

    public sealed class LauncherConnectionInformation
    {
        public string ShortName { get; }
        public string InstallationName { get; }
        public string Heading => ShortName + " connection";
        public string HowItWorks { get; }
        public string Permissions { get; }
        public string Removal { get; }
        public bool Beta { get; }
        private LauncherConnectionInformation(string shortName, string installationName, string how, string permissions, string removal, bool beta = false)
        { ShortName = shortName; InstallationName = installationName; HowItWorks = how; Permissions = permissions; Removal = removal; Beta = beta; }

        public static LauncherConnectionInformation Steam { get; } = new LauncherConnectionInformation("Steam", "Steam",
            "Uses Steam UI debugging to observe updates and manage supported downloads. Setup creates .cef-enable-remote-debugging and an ownership record in the Steam folder. Restart Steam once after setup; later starts reuse this setting.",
            "Windows may request administrator permission to change the setup file. No Steam password or API key is requested.",
            "Remove connection deletes an unchanged marker created by this plugin. Pre-existing, legacy or changed setup is preserved; plugin observation still stops. A running Steam client must restart to disable active debugging; a closed client needs no restart. Non-empty files or folders are preserved. Uninstalling the plugin alone does not remove it.");
        public static LauncherConnectionInformation Epic { get; } = new LauncherConnectionInformation("Epic", "Epic Games Launcher",
            "Uses Epic UI debugging to observe supported update activity. Setup adds local debugging arguments to existing launcher/game shortcuts, game links and an existing Windows startup entry. It does not turn Windows startup on. Restart once after setup. Opening the executable directly, Playnite’s Open Epic command or replaced shortcuts may bypass the connection.",
            "Shared shortcuts may need administrator permission. No Epic password or API key is requested.",
            "Remove connection restores saved startup values where they still match the plugin’s changes. Later user edits are preserved and incomplete cleanup is reported. A running Epic client must restart to disable active debugging; a closed client needs no restart. Uninstalling the plugin alone does not undo setup.", true);
    }
}
