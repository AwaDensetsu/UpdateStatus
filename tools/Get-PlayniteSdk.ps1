param()
$ErrorActionPreference = 'Stop'
$sdkProjectRoot = Split-Path $PSScriptRoot -Parent
$sdkTargetFolder = Join-Path $sdkProjectRoot 'tools\playnite-sdk\6.13.0'
$sdkPackagePath = Join-Path $sdkTargetFolder 'playnitesdk.6.13.0.nupkg'
$sdkExpectedHash = '8A7A93991576EB0A2A66B5E50B883B3A91ACCA203E4D83ACEAB3EF2F796C3BB1'
New-Item -ItemType Directory -Path $sdkTargetFolder -Force | Out-Null
if (!(Test-Path -LiteralPath $sdkPackagePath)) {
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/playnitesdk/6.13.0/playnitesdk.6.13.0.nupkg' -OutFile $sdkPackagePath
}
if ((Get-FileHash -LiteralPath $sdkPackagePath -Algorithm SHA256).Hash -ne $sdkExpectedHash) { throw 'Unexpected official SDK package hash' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sdkPackageZip = [IO.Compression.ZipFile]::OpenRead($sdkPackagePath)
try {
    $sdkDllEntry = $sdkPackageZip.GetEntry('lib/net462/Playnite.SDK.dll')
    if (!$sdkDllEntry) { throw 'Expected SDK assembly absent' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($sdkDllEntry,(Join-Path $sdkTargetFolder 'Playnite.SDK.dll'),$true)
} finally { $sdkPackageZip.Dispose() }
# Development reference only. The SDK is supplied by Playnite at runtime.
Get-FileHash -LiteralPath (Join-Path $sdkTargetFolder 'Playnite.SDK.dll') -Algorithm SHA256
