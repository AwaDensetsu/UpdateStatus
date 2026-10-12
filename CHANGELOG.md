# Changelog

## 1.2.0

Changes since 1.1.0:

- Add optional Epic Games update observation (beta), off by default for new installations. Existing saved choices are preserved; Epic percentages and download controls remain unavailable.
- Add separate General, Steam and Epic settings pages with matching connection cards.
- Add launcher setup, confirmed restart and connection removal controls.
- Clear a launcher's update information when its connection is removed, and preserve unrelated or changed setup.
- Improve Steam launch cancellation, observer error handling and connection/session checks.
- Reduce temporary allocations from repeated Steam process discovery.
- Expand automated tests and update setup instructions.

## 1.1.0

- Replace native Steam DLL/binary access with a local Steam UI connection.
- Add first-time connection setup.
- Improve Pause/Resume, Close, game switching and Steam restart recovery.
- Move to standard NuGet/MSBuild, .NET Framework 4.6.2 and automated Windows checks.

## 1.0.0 — Initial release

- Steam update status, download metrics and separate file progress.
- Cover/list indicators, Pause/Resume and launch after updating.
- Queue removal, remembered update requirements and shutdown preferences.
