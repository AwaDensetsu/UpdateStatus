<p align="center">
  <img src="assets/update-status-banner.svg" alt="Update Status — game updates and download controls for Playnite" width="1200">
</p>

<p align="center">
  <a href="https://github.com/AwaDensetsu/UpdateStatus/releases/latest"><strong>Download</strong></a> ·
  <a href="#installation">Installation</a> ·
  <a href="#features">Features</a> ·
  <a href="#controls">Controls</a> ·
  <a href="#settings">Settings</a> ·
  <a href="#future-plans">Future plans</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

Update Status shows which games need an update and what is happening while they update. Follow downloads from your library, pause or resume them, and let the selected game start when its update finishes.

**Upgrading from 1.0.0?** Version 1.1.0 uses a new Steam UI connection for live progress and controls. After installing, open the plugin settings and check **Connected**. If setup is needed, follow the [first-time setup instructions](#first-time-setup).

## Features

- **Live progress:** downloaded size, total size, download speed and estimated time remaining.
- **Separate progress bars:** downloading data and updating files are displayed separately.
- **Library indicators:** Update Required, Downloading, Paused and processing stages on recognizable covers and game lists.
- **Download controls:** Pause, Resume and a close button inside Playnite.
- **Launch after updating:** wait for the selected game's required update, then continue its normal Playnite launch.
- **Background monitoring:** show active download progress even when the update started before Playnite opened.
- **Remembered updates:** keep confirmed Update Required indicators between restarts until fresh data confirms completion.
- **Automatic setup:** find and remember the Steam installation folder on each PC.

## Installation

1. Download the `.pext` installer from the latest release using the link above.
2. Open Playnite in **Desktop mode**.
3. Drag the `.pext` file into the Playnite window, confirm installation and restart Playnite when prompted.
4. Open **Main menu → Add-ons → Extension settings → Generic → Update Status** to configure the plugin.

Steam games must be added through Playnite's Steam library integration.

## First-time setup

Open the plugin settings and check the Steam folder. **Steam found automatically** means the installation folder was detected. If it was not found, use **Browse** to select the installation folder containing `steam.exe` and `steamclient64.dll`.

For live progress and controls, check **Connected** in settings. If setup is needed, click **Connect to Steam**, then close and reopen Steam yourself. Windows may ask for administrator permission to create the setup file. This enables Steam UI debugging on this PC: local programs can inspect and control the Steam UI through that interface. It stays enabled after Playnite closes; remove `.cef-enable-remote-debugging` from the Steam folder and restart Steam to disable it. The plugin connects only to a Steam-owned loopback endpoint. No account login or API key is required. The connection details and instructions for disabling access are available under **More information** in settings.

Start a Steam game from Playnite as usual. When a supported required update is detected, the plugin manages the update before continuing the game launch. You can browse your library while it runs.

## Controls

These controls apply when the plugin is managing a supported Steam update.

- **Play** — Start the selected game's required update and launch the game when it finishes.
- **Pause** — Pause all Steam downloads and cancel the selected game's pending launch.
- **Resume** — Resume downloads and restore the selected game's pending launch.
- **×** — Pause all Steam downloads, remove the selected update from the queue, cancel its pending launch and return to the library. Pending launch cancellation does not depend on removal succeeding. If removal fails, the card stays visible for an explicit retry; library return waits for confirmation.
- **Play another game** — Remove the previous selected update from the queue and cancel its pending launch before starting the new selection.
- **Browse the library** — Keep the current update and pending launch active.

If close is rejected or cannot be confirmed, the card remains available for retry and the pending game launch stays cancelled. The displayed download state comes from observed Steam data.

The percentage shown on covers and lists measures **downloaded data**. After downloading completes, the indicator shows the current processing stage. The detailed card keeps downloading and file work as separate progress bars.

## Settings

Settings apply across Desktop and Fullscreen.

| Setting | Purpose |
| --- | --- |
| **Enable plugin** | Turn Update Status on or off. |
| **Steam folder** | Use the automatically detected folder or select it manually. Clear a manual choice to restore automatic detection. |
| **Polling interval** | Choose how often the selected update is checked: 500–2000 ms, with 1000 ms as the default. |
| **Download speed units** | Show bytes per second (KB/s, MB/s) or bits per second (kbps, Mbps). |
| **Show update status** | Show or hide the automatic detailed update card. |
| **Show update indicators on covers and lists** | Show or hide library update indicators. Also available from the extension menu. |
| **Restore Playnite if Steam takes focus during an update** | Bring Playnite back to the foreground during the update. |
| **When Playnite closes** | Keep the current download state, pause and keep the selected update in the queue, or pause and remove it from the queue. |

The default close behavior is **Keep current state**. These options apply to normal Playnite shutdown while the plugin is managing a selected update.

> Tested with the current versions of Steam and Playnite at the time of testing. Behavior on older or future versions has not been verified.

If an established selected update stops providing fresh data, the card shows **Update data unavailable** while the reader retries. After one minute without fresh state, the pending launch is cancelled and the card asks you to press Play again. Initial connection setup has a three-minute limit. After a cancelled Play, including a failed close request, one minute without fresh update state also produces an unavailable error. Fresh queue confirmation can still complete Close, and restored observations never revive that cancelled launch. These limits do not apply while an update continues providing fresh state, including paused updates and file processing. Steam exit or restart also requires a new Play request for the selected game. Terminal interruption returns the held SDK startup callback with cancellation so Playnite can clear Launching; the error card remains available. Recoverable Steam update errors retain their waiting behavior.

## Privacy

Progress is read through Steam UI callbacks, which are private interfaces and may change with Steam updates. If those callbacks are unavailable, the plugin shows a waiting or unavailable state. This has been tested on the local Windows client; other Steam versions still need testing.

Settings, detected folders and update history are stored locally in Playnite's plugin data folder. Update Status has no telemetry or remote service and does not ask for passwords, a Steam API key or a separate account.

## Future plans

My goal is to keep improving Update Status, refining the experience and adding useful features based on user feedback. I also plan to expand support to more launchers through Playnite's library integrations, bringing the same update monitoring and controls wherever supported.

## Feedback

Found a problem? Open an issue in this repository with your Playnite version, Steam version, Desktop or Fullscreen mode, theme name and steps to reproduce it. A screenshot of the problem is helpful.

<details>
<summary><strong>Build from source</strong></summary>

The plugin targets Playnite SDK 6.13 and requires a host with SDK 6.13 or a compatible newer 6.x version. Custom launch actions or requests already forwarded to Steam may not support the same launch cancellation behavior.

On Windows, install the .NET SDK 8.0 or newer, or use Visual Studio with a compatible .NET SDK and the .NET desktop development workload. The projects target .NET Framework 4.6.2; the build SDK is only a developer tool. Run this from the repository folder:

```shell
dotnet build UpdateStatus.sln --configuration Release
```

NuGet restores PlayniteSDK and the .NET Framework reference assemblies through PackageReference. Internet access is needed for the first restore. The checked-in packages.lock.json files record the resolved dependencies; use dotnet restore UpdateStatus.sln --locked-mode to verify them. In VS Code, Ctrl+Shift+B runs the included Debug build task; Release and packaging are available through Run Task.

To create an installer, pass your Playnite installation folder, which contains `Toolbox.exe`:

```shell
dotnet msbuild src/UpdateStatus.csproj -restore -target:PackageExtension -property:Configuration=Release -property:PlayniteDir="C:\Path\To\Playnite"
```

The installer is written to `artifacts/testing`. The runtime uses the SDK supplied by Playnite.

The **Windows build** GitHub Actions workflow restores locked dependencies, compiles Release and runs the included NUnit and mocked Steam UI bridge tests on Windows. It uses .NET SDK 10 and Node.js 24 as developer tools; the plugin still targets .NET Framework 4.6.2. The workflow does not publish releases. See [tests/README.md](tests/README.md) for commands and coverage. Real Steam behavior and host-dependent UI checks still require separate manual/local verification.

</details>

<details>
<summary><strong>How the plugin works</strong></summary>

- `Plugin` receives Playnite SDK events and wires the services together. `LaunchCoordinator` owns the selected game's pending Play request, cancellation and SDK replay after Resume.
- `SteamUpdateCatalog` scans local Steam library metadata and observes the download queue. Confirmed update history is stored in Playnite's plugin data folder.
- `SteamUpdateReader` runs separately from Playnite and connects to a verified Steam-owned loopback CEF endpoint. Steam UI callbacks supply selected progress and queue snapshots. The parent validates identity and freshness before using them.
- `SteamSelectedUpdateSession` keeps queue commands separate from launch intent. Completion requires fresh state for the selected game and finished file processing; download bytes reaching 100% are insufficient. Failed close requests can be retried without reviving the cancelled Play.
- `UpdateMonitor` serializes observation and checks game/session identity before accepting command results. The UI displays these snapshots in the detailed card, cover indicators and game lists.
- `Settings`, `SettingsViewModel` and `SettingsView` separate saved preferences, editing/setup behavior and XAML layout. Preferences are shared between Desktop and Fullscreen.

The Steam UI methods are internal interfaces. Missing or changed callbacks produce unavailable data rather than assumed completion. Helper reconnects can restore observations; cancelled launches stay cancelled. The reader never loads Steam native DLLs or inspects their machine-code addresses.

</details>

## License

Licensed under the [MIT License](LICENSE).


## Credits

I originally made Update Status for my own use and decided to share it in case others find it useful. I'm still learning to code, so feedback, bug reports and suggestions are welcome.

The code was developed with assistance from **OpenAI Codex**.
