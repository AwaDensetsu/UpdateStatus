# Development checks

These Windows/SDK/WPF integration checks are author-side and are excluded from installers. They are included in the public source alongside the NUnit and JavaScript tests; see the parent README.

## Unit tests

From the project root, run:

```text
dotnet test tests/Tests.csproj --configuration Release
```

The NUnit tests cover Steam and Epic parsing/transport, progress and presentation, reader lifetime, settings and connection setup/removal, repeated restart validation, selected-session failure recovery and launch cancellation. Each check is independently discoverable, with its original description retained. They use synthetic samples and do not require a Steam installation or an active download.

The setup checks also reject delayed error replies after a folder switch or a round trip back to the original folder. Selected-session checks cover failed close retries, concurrent clicks, observed pause state and bounded data-loss recovery without a real Steam connection.

Run one group or list individual tests:

```text
dotnet test tests/Tests.csproj --configuration Release --filter TestCategory=SteamProtocol
dotnet test tests/Tests.csproj --configuration Release --list-tests
```

## Playnite and UI integration

The retained integration checks need Windows, an installed Playnite folder and a 32-bit WPF process. They exercise SDK callbacks, cancellation acknowledgements, Pause/Resume, queue handoff, settings and cover/list rendering. Keep this process separate from the NUnit host so dispatcher and SDK state are isolated.

```text
dotnet build tests/Integration/IntegrationChecks.csproj --configuration Release
bin/Tests/Tests.exe --playnite="<Playnite installation folder>"
```

The Windows/Playnite SDK/WPF suite includes Epic settings, connection removal and restart lifecycle fixtures. Native Steam session checks compare the optimized snapshot with managed inventory and verify disposable process exit/replacement and repeated enumeration cleanup. Per-version executed totals are recorded in the validation report. SDK-dependent serialization and edit-session checks run here, outside the isolated NUnit host. An individual integration run still supports the existing switches, including `--handoff-only --sdk-only`, `--resume-ui-only`, `--shutdown-only`, `--pause-confirmation-only` and `--background-progress-only`. Provide `--playnite` for groups that use the SDK fixtures.

The two `--reader-lifetime-only` checks launch a bounded, synthetic child of the private runner. They verify cleanup after initialization failure and Dispose without connecting to Steam. The fixture checks its parent, has a 20-second deadline and is stopped by its owner.

These checks use temporary files and fake SDK/download controls. The optional `--local-steam` and `--native-steam` switches remain explicit, separate read-only checks against a local Steam installation; they are not part of the default test run.

## Steam UI bridge

```text
node tests/SteamUiBridgeChecks.cjs
```

The 16 JavaScript checks use mocked Steam callbacks. They verify local client identity, command arguments, queue behavior, remote-client rejection and subscription cleanup without contacting Steam.

## Layout

- `Unit/`: independently discoverable NUnit checks.
- `Support/`: shared synthetic Steam samples used by both runners.
- `Integration/Launch/`: Playnite startup, replacement, Pause/Resume and shutdown.
- `Integration/Controls/`: command confirmation and queue removal.
- `Integration/Steam/`: mapping, catalog, setup and selected-session checks.
- `Integration/Settings/`: persistence, folder selection and update history.
- `Integration/UI/`: status cards, covers and list/table layouts.
- `Integration/Support/`: fake Playnite API, WPF helpers and common assertions.
- `Integration/Benchmarks/`: optional author benchmarks, outside the regression run.

NuGet dependencies use normal PackageReference entries and lock files. For a reproducible restore, add `-p:RestoreLockedMode=true` to the build/test commands. Test output and reports belong under ignored `bin/` and `artifacts/`, never in publish.
