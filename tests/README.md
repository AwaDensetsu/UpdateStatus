# Development checks

Run from the repository root on Windows with a compatible .NET SDK. JavaScript checks need Node.js. NuGet access is needed for the first restore.

## Unit and bridge tests

```text
dotnet restore UpdateStatus.sln --locked-mode
dotnet build UpdateStatus.sln --configuration Release --no-restore
dotnet test tests/Tests.csproj --configuration Release --no-build --no-restore
node tests/SteamUiBridgeChecks.cjs
node tests/EpicUiBridgeChecks.cjs
```

These use synthetic state, temporary installation fixtures and mocked launcher controls. They cover parsing, progress, launch intent/cancellation, errors, session identity, setup/removal and stale replies. They do not require an installed Playnite, Steam account or active download and do not change real launcher configuration/downloads. Windows file-identity fixtures use actual Windows filesystem APIs on their temporary files. Node checks use mocked Steam/Epic UI APIs.

The Windows CI workflow runs the build, NUnit and both JavaScript suites. It does not run the separate host-dependent integration project or perform real launcher validation.

## Playnite SDK and WPF integration

With an installed Playnite folder on Windows:

```text
dotnet build tests/Integration/IntegrationChecks.csproj --configuration Release -p:RestoreLockedMode=true
bin/Tests/Tests.exe --playnite="C:\Path\To\Playnite"
```

This separate 32-bit executable covers SDK startup callbacks, Desktop/Fullscreen UI, settings and owned disposable Windows-process lifecycles. Native snapshot checks read process metadata; launcher commands/setup use fixtures. The default suite does not restart real launchers. See [Integration/README.md](Integration/README.md) for focused commands.

## Latest local validation

For 1.2.0, the development run passed **332 NUnit cases, 16 Steam bridge checks, 21 Epic bridge checks and 259 Windows/SDK/WPF checks**, with a clean Release build. These are local results, not a claim that GitHub CI or another machine has run them. Source ZIP/installer integrity was also checked.

The owner confirmed the preceding 1.1.17 development build worked in normal use. The 1.2.0 package still needs an installation/upgrade smoke test. Genuine Epic game updates, connected/active Epic performance, unusual multi-user/RDP setups and long sessions still need separate validation. Automated tests do not guarantee behavior on every launcher version or custom theme.

Test sources, temporary reports and development dependencies are excluded from the installer. Generated output belongs in ignored `bin`, `obj` and `artifacts` directories.
