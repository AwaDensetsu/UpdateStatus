# Changelog

## 1.1.0

- Read live progress and download controls through Steam UI callbacks instead of native DLLs and binary addresses.
- Add one-time Steam connection setup in settings.
- Improve Pause, Resume, Close and game switching without restoring cancelled launches.
- Recover from Steam shutdown, restart and missing data without leaving Playnite stuck on Launching.
- Reduce repeated catalog work and clean up the launch lifecycle code.
- Use standard NuGet/MSBuild tooling with .NET Framework 4.6.2 and include automated tests and Windows CI.

## 1.0.0 — Initial release

Steam is currently the supported launcher.

- Show update status in Desktop and Fullscreen.
- Display download size, speed, remaining time and separate file update progress.
- Show update indicators on game covers and lists.
- Pause and resume downloads, then launch the selected game when its update finishes.
- Use × to pause downloads, remove the selected update from the queue and cancel its pending launch.
- When starting another game, remove the previous selected update from the queue and cancel its pending launch.
- Remember games that need updates between restarts.
- Find the Steam installation folder automatically, with manual selection available.
- Choose speed units, indicator visibility and what happens to the selected update when Playnite closes.
