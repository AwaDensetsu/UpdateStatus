param([string]$Version = '1.0.3')
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $project 'tools\reference-assemblies'
$target = Join-Path $cache 'build\.NETFramework\v4.8'
if (Test-Path (Join-Path $target 'mscorlib.dll')) { Write-Output $target; exit 0 }
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache 'net48.zip'
Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/$Version/microsoft.netframework.referenceassemblies.net48.$Version.nupkg" -OutFile $archive
Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
Write-Output $target
