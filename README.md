<p align="center">
  <img src="assets/update-status-banner.svg" alt="Update Status — game updates and download controls for Playnite" width="1200">
</p>

<p align="center">
  <a href="releases/UpdateStatus_1.0.0.pext?raw=true"><strong>Download v1.0.0</strong></a> ·
  <a href="#installation">Installation</a> ·
  <a href="#features">Features</a> ·
  <a href="#controls">Controls</a> ·
  <a href="#settings">Settings</a> ·
  <a href="#future-plans">Future plans</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

Update Status shows which games need an update and what is happening while they update. Follow downloads from your library, pause or resume them, and let the selected game start when its update finishes.

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

1. Download **UpdateStatus_1.0.0.pext** using the link above.
2. Open Playnite in **Desktop mode**.
3. Drag the `.pext` file into the Playnite window, confirm installation and restart Playnite when prompted.
4. Open **Main menu → Add-ons → Extension settings → Generic → Update Status** to configure the plugin.

Steam games must be added through Playnite's Steam library integration.

## First-time setup

Open the plugin settings and check the Steam folder. **Steam found automatically** means setup is complete. If it was not found, use **Browse** to select the installation folder containing `steam.exe` and `steamclient64.dll`.

Start a Steam game from Playnite as usual. When a supported required update is detected, the plugin manages the update before continuing the game launch. You can browse your library while it runs.

## Controls

These controls apply when the plugin is managing a supported Steam update.

- **Play** — Start the selected game's required update and launch the game when it finishes.
- **Pause** — Pause all Steam downloads and cancel the selected game's pending launch.
- **Resume** — Resume downloads and restore the selected game's pending launch.
- **×** — Pause all Steam downloads, remove the selected update from the queue, cancel its pending launch and return to the library.
- **Play another game** — Remove the previous selected update from the queue and cancel its pending launch before starting the new selection.
- **Browse the library** — Keep the current update and pending launch active.

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

## Privacy

Settings, detected folders and update history are stored locally in Playnite's plugin data folder. Update Status has no telemetry or remote service and does not ask for passwords, a Steam API key or a separate account.

## Future plans

My goal is to keep improving Update Status, refining the experience and adding useful features based on user feedback. I also plan to expand support to more launchers through Playnite's library integrations, bringing the same update monitoring and controls wherever supported.

## Feedback

Found a problem? Open an issue in this repository with your Playnite version, Steam version, Desktop or Fullscreen mode, theme name and steps to reproduce it. A screenshot of the problem is helpful.

<details>
<summary><strong>Build from source</strong></summary>

The plugin targets Playnite SDK 6.13 and requires a host with SDK 6.13 or a compatible newer 6.x version. Custom launch actions or requests already forwarded to Steam may not support the same launch cancellation behavior.

On Windows, run this from the repository folder in PowerShell:

```powershell
.\tools\Build.ps1
```

The build script restores the official Playnite SDK reference and .NET Framework reference assemblies when needed. Internet access is needed for the first dependency download.

To create an installer, pass your Playnite installation folder, which contains `Toolbox.exe`:

```powershell
.\tools\Package.ps1 -PlayniteDir 'C:\Path\To\Playnite'
```

The installer is written to `artifacts/testing`. The runtime uses the SDK supplied by Playnite.

</details>

## License

Licensed under the [MIT License](LICENSE).

The bundled Iced dependency includes its [own license](tools/runtime-dependencies/iced-1.21.0/LICENSE.txt).

## Credits

The code was developed with assistance from **OpenAI Codex**.
