param([string]$PlayniteDir = 'C:\Playnite', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
if (!$SkipBuild) { & "$PSScriptRoot\Build.ps1" -PlayniteDir $PlayniteDir }
# Read the reference table only; do not resolve types or instantiate the plugin.
# LoadFile supports both Windows PowerShell and PowerShell 7.
$packageAssembly = [Reflection.Assembly]::LoadFile((Join-Path $project 'bin\Release\PlayniteUpdateStatus.dll'))
$packageReferences = $packageAssembly.GetReferencedAssemblies()
$packageSdk = @($packageReferences | Where-Object { $_.Name -eq 'Playnite.SDK' })
if ($packageSdk.Count -ne 1 -or $packageSdk[0].Version -ne [Version]'6.13.0.0') { throw 'Package must reference the minimum supported SDK 6.13' }
if (@($packageReferences | Where-Object { $_.Name -like 'Playnite*' -and $_.Name -ne 'Playnite.SDK' }).Count -ne 0) { throw 'Private Playnite host reference is forbidden' }
$stagingRoot = Join-Path $project 'artifacts\staging'
$stage = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
$out = Join-Path $project 'artifacts\testing'
New-Item -ItemType Directory -Path $out -Force | Out-Null
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Copy-Item "$project\bin\Release\PlayniteUpdateStatus.dll", "$project\src\extension.yaml", "$project\LICENSE" -Destination $stage
    Copy-Item "$project\bin\Release\SteamUpdateReader.exe" -Destination $stage
    Copy-Item "$project\bin\Release\Iced.dll" -Destination $stage
    Copy-Item "$project\tools\runtime-dependencies\iced-1.21.0\LICENSE.txt" -Destination (Join-Path $stage 'Iced.LICENSE.txt')
    & "$PlayniteDir\Toolbox.exe" pack $stage $out
    if ($LASTEXITCODE -ne 0) { throw 'Playnite Toolbox package failed' }
    $manifestText = Get-Content -LiteralPath "$project\src\extension.yaml" -Raw
    $packageId = [regex]::Match($manifestText,'(?m)^Id:\s*(\S+)').Groups[1].Value
    $packageVersion = [regex]::Match($manifestText,'(?m)^Version:\s*(\S+)').Groups[1].Value.Replace('.','_')
    $packageFile = [IO.Path]::GetFullPath((Join-Path $out ($packageId + '_' + $packageVersion + '.pext')))
    if (!$packageFile.StartsWith([IO.Path]::GetFullPath($out).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes testing folder' }
    if (!(Test-Path -LiteralPath $packageFile)) { throw 'No extension package generated' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageZip = [IO.Compression.ZipFile]::OpenRead($packageFile)
    try {
        $packageEntries = @($packageZip.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        $expectedEntries = @('PlayniteUpdateStatus.dll','SteamUpdateReader.exe','Iced.dll','Iced.LICENSE.txt','extension.yaml','LICENSE') | Sort-Object
        if (Compare-Object $expectedEntries $packageEntries) { throw 'Unexpected package contents' }
    } finally { $packageZip.Dispose() }
    $packagePublicVersion = $packageVersion.Replace('_','.')
    $packageSharedFile = Join-Path $out ("UpdateStatus_$packagePublicVersion.pext")
    Copy-Item -LiteralPath $packageFile -Destination $packageSharedFile
    if ((Get-FileHash -LiteralPath $packageFile).Hash -ne (Get-FileHash -LiteralPath $packageSharedFile).Hash) { throw 'Testing package copy differs' }
    Remove-Item -LiteralPath $packageFile
    Write-Output 'Testing package prepared; publish folder unchanged'
    Get-FileHash -LiteralPath $packageSharedFile -Algorithm SHA256
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $resolvedStagingRoot = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\')
    if (!$resolvedStage.StartsWith($resolvedStagingRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging cleanup target' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
