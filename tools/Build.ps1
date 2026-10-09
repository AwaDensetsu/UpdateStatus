param([string]$PlayniteDir = 'C:\Playnite', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$buildTempDirectory = Join-Path $project 'artifacts\build-temp'
New-Item -ItemType Directory -Path $buildTempDirectory -Force | Out-Null
$buildOriginalTemp = $env:TEMP; $buildOriginalTmp = $env:TMP
$env:TEMP = $buildTempDirectory; $env:TMP = $buildTempDirectory
try {
    $testProject = Join-Path $project 'tests\Tests.csproj'
    $runTests = !$SkipTests -and (Test-Path -LiteralPath $testProject)
    if ($runTests) {
        $sdk = Join-Path $PlayniteDir 'Playnite.SDK.dll'
        if (!(Test-Path -LiteralPath $sdk)) { throw "Playnite SDK not found: $sdk. Pass -PlayniteDir." }
    }
    $minimumSdk = Join-Path $project 'tools\playnite-sdk\6.13.0\Playnite.SDK.dll'
    if (!(Test-Path -LiteralPath $minimumSdk)) { & "$PSScriptRoot\Get-PlayniteSdk.ps1" | Out-Null }
    $referenceRoot = Join-Path $project 'tools\reference-assemblies\build\'
    if (!(Test-Path (Join-Path $referenceRoot '.NETFramework\v4.8\mscorlib.dll'))) { & "$PSScriptRoot\Get-ReferenceAssemblies.ps1" | Out-Null }
    $msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'
    & $msbuild "$project\src\UpdateStatus.csproj" /nologo /verbosity:minimal /p:Configuration=Release "/p:PlayniteDir=$PlayniteDir" "/p:TargetFrameworkRootPath=$referenceRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
    & $msbuild "$project\steam-reader\SteamReader.csproj" /nologo /verbosity:minimal /p:Configuration=Release "/p:TargetFrameworkRootPath=$referenceRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Steam reader build failed' }
    Copy-Item "$project\bin\SteamReader\SteamUpdateReader.exe" -Destination "$project\bin\Release\SteamUpdateReader.exe"
    if ($runTests) {
        & $msbuild $testProject /nologo /verbosity:minimal /p:Configuration=Release "/p:PlayniteDir=$PlayniteDir" "/p:TargetFrameworkRootPath=$referenceRoot"
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
        Copy-Item -LiteralPath $sdk -Destination "$project\bin\Tests\Playnite.SDK.dll"
        Copy-Item "$project\bin\SteamReader\SteamUpdateReader.exe" -Destination "$project\bin\Tests\SteamUpdateReader.exe"
        & "$project\bin\Tests\Tests.exe" --local-steam "--playnite=$PlayniteDir"
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
} finally {
    $env:TEMP = $buildOriginalTemp; $env:TMP = $buildOriginalTmp
}
