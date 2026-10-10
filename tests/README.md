# Tests

The source archive includes 105 NUnit tests and 16 JavaScript bridge checks. They use synthetic data and mocked controls, without a Steam account, installed Playnite or an active download.

Run from the repository root on Windows with a compatible .NET SDK. The JavaScript checks need Node.js; CI uses version 24. NuGet access is needed for the initial restore.

```shell
dotnet restore UpdateStatus.sln --locked-mode
dotnet build UpdateStatus.sln --configuration Release --no-restore
dotnet test tests/Tests.csproj --configuration Release --no-build --no-restore
node tests/SteamUiBridgeChecks.cjs
```

To inspect or run a smaller group:

```shell
dotnet test tests/Tests.csproj --configuration Release --list-tests
dotnet test tests/Tests.csproj --configuration Release --filter TestCategory=SelectedSession
```

The NUnit cases cover parsing, progress presentation, download/file-work separation, transfer metrics, settings setup, borrowed reader freshness, failed close retries, data-loss deadlines including continuous observer errors, and cancelled launch intent, SDK callback return and UI dispatcher unwinding, expired catalog data, inventory replacement and recovery. Steam exit/restart behavior is simulated with independent client identities. The bridge checks cover callback shapes, local versus remote clients, command arguments, queue scope and subscription cleanup.

Temporary installation fixtures contain harmless placeholder files. They are never executed or loaded. Setup actions and administrator prompts are mocked; these tests do not modify a real Steam installation or change its downloads.

The Windows build workflow restores locked dependencies, compiles Release and runs both suites. Reports are written under ignored `artifacts/`. Test sources and dependencies are excluded from the `.pext` installer.

These checks do not replace manual testing with actual Steam updates, restart timing, Playnite views and themes. Windows/SDK/UI integration checks requiring a local Playnite installation are maintained separately from this source snapshot.
