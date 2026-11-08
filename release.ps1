# Builds Say Again and packages it for a GitHub release / the vatSys Launcher.
# The launcher unzips the release asset into Profiles\<profile>\Plugins\Say-Again (or bin\Plugins),
# so the zip holds the plugin DLL (and licence) at its root.
#
#   .\release.ps1                 -> artifacts\Say-Again-v<version>.zip
param([string]$VatSysBin = 'C:\Program Files (x86)\vatSys\bin')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

dotnet build "$root\src\AFVReplay\AFVReplay.csproj" -c Release -p:VatSysBin="$VatSysBin" | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$dll = "$root\src\AFVReplay\bin\Release\net472\AFVReplay.dll"
$version = (Get-Item $dll).VersionInfo.FileVersion            # e.g. 0.1.0.0
$short = ($version -split '\.')[0..2] -join '.'               # 0.1.0

$stage = Join-Path $root 'artifacts\stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll $stage
Copy-Item "$root\LICENSE" (Join-Path $stage 'LICENSE.txt')

$zip = Join-Path $root "artifacts\Say-Again-v$short.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
"Packaged $zip (AFVReplay.dll $version)"
