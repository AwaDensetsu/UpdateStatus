# Epic Games observation — beta

Epic support uses the normal Epic Games Launcher and Playnite's official Epic library integration. It observes update requirements and activity; downloads and game launches remain managed by Epic.

Genuine Epic update validation is still pending. Percentages, speed, file-work stages and Pause/Resume/Cancel are unavailable. Closing the status card only hides it. Switching games or closing Playnite leaves Epic downloads running.

## Connection and startup

Epic observation is off by default on new installations. Existing users keep their saved choice. In the Epic settings page, enable **Enable Epic observation (beta)**, use **Set up connection**, restart Epic when requested and wait for **Connected**. Setup adds local UI debugging arguments to supported existing shortcuts, the current user's game links and an existing Windows startup entry. It does not enable Windows startup if it was disabled. Shared shortcut changes may require administrator permission.

After setup, supported startup routes reuse the connection. Opening the `.exe` directly, using Playnite's **Open Epic** command or using shortcuts replaced by a launcher update can bypass it. If Epic is running without a connection, use the addon restart action when convenient and check the status again.

Restart asks for confirmation. If normal closure fails, the Epic restart action may force-close the verified launcher process before reopening it. Downloads or games can be interrupted.

## Remove the connection

Open **Advanced information & permissions → Remove connection**. Original startup values are restored where they still match the plugin's changes. Later edits are preserved, and incomplete cleanup is reported. Restart a running Epic launcher afterwards; a closed launcher needs no restart.

Original startup values are stored locally in `%LOCALAPPDATA%\UpdateStatus\epic-connection.json` until cleanup completes. Use **Remove connection** before uninstalling; uninstalling or closing Playnite alone does not restore startup routes.

Disabling **Enable Epic observation (beta)** stops observation without removing setup. Successful connection removal also clears Epic observations and keeps monitoring disabled until you explicitly reconnect.

## Feedback

Report the connection status, launch route and whether the activity was a normal game update or a content/language change. Include the launcher/plugin versions and steps to reproduce it. A settings screenshot and an Epic download screenshot are useful.
