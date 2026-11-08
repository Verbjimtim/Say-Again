# Builds Say Again and copies it into vatSys's plugin folder (Plugins\Say-Again, the same
# folder the vatSys Launcher uses). Removes the old Plugins\AFVReplay folder from earlier builds.
# vatSys only loads plugins at startup, so restart vatSys afterwards.
#
#   .\install.ps1                 -> all profiles (vatSys\bin\Plugins, asks for admin)
#   .\install.ps1 -Profile        -> current profile only (no admin needed)
#   .\install.ps1 -Uninstall      -> removes it from both places
param(
    [switch]$Profile,
    [switch]$Uninstall,
    [string]$VatSysBin = 'C:\Program Files (x86)\vatSys\bin'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Get-ProfilePluginDir {
    $cfg = Get-ChildItem "$env:LOCALAPPDATA\Sawbe.com" -Recurse -Filter user.config -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $cfg) { throw 'Could not find vatSys user settings to locate the current profile.' }
    $xml = [xml](Get-Content $cfg.FullName)
    $dataset = ($xml.SelectNodes('//setting[@name="DatasetPath"]/value') | Select-Object -First 1).InnerText
    if (-not $dataset) { throw 'vatSys DatasetPath setting not found.' }
    Join-Path $dataset 'Plugins\Say-Again'
}

$global = Join-Path $VatSysBin 'Plugins\Say-Again'
# Earlier development builds installed here; two copies would both load.
function Get-LegacyDirs { @((Join-Path $VatSysBin 'Plugins\AFVReplay'), ((Split-Path (Get-ProfilePluginDir) -Parent) + '\AFVReplay')) }

if ($Uninstall) {
    foreach ($d in @($global, (Get-ProfilePluginDir)) + (Get-LegacyDirs)) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force; "Removed $d" }
    }
    return
}

if (Get-Process vatSys -ErrorAction SilentlyContinue) {
    Write-Warning 'vatSys is running. The plugin will load the next time vatSys starts.'
}

dotnet build "$root\src\AFVReplay\AFVReplay.csproj" -c Release -p:VatSysBin="$VatSysBin" | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$dll = "$root\src\AFVReplay\bin\Release\net472\AFVReplay.dll"

$target = if ($Profile) { Get-ProfilePluginDir } else { $global }
foreach ($old in Get-LegacyDirs) {
    if ((Test-Path $old) -and ($old -notlike "$VatSysBin*")) { Remove-Item $old -Recurse -Force; "Removed old install $old" }
    elseif (Test-Path $old) { Write-Warning "Old install at $old would load twice: remove it (needs admin) or run -Uninstall as admin." }
}
if ($Profile) {
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $dll $target -Force
} else {
    # Program Files needs admin.
    $cmd = "New-Item -ItemType Directory -Force '$target' | Out-Null; Copy-Item '$dll' '$target' -Force"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $cmd
}
if (Test-Path (Join-Path $target 'AFVReplay.dll')) { "Installed to $target" } else { throw "Copy to $target failed" }
