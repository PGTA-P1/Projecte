# Builds installer\output\ProjecteP1-setup.exe:
#   1. publishes the app self-contained for 64-bit Windows (.NET included) into installer\publish
#   2. packs it with Inno Setup (ISCC.exe)
# Needs: the .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup) and Projecte1(WPF)\arcgis.keys.dat (the
# scrambled keys, in the repository), which the setup includes so the map works without anyone generating a key.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.0]
# Publish it by attaching installer\output\ProjecteP1-setup.exe to a GitHub release (keep that file name).

param([string]$Version = '1.0')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path $here -Parent
$project = Join-Path $root 'Projecte1(WPF)\Projecte1(WPF).csproj'
$keys = Join-Path $root 'Projecte1(WPF)\arcgis.keys.dat'
$publish = Join-Path $here 'publish'

if (-not (Test-Path $keys)) {
    throw "Projecte1(WPF)\arcgis.keys.dat is missing: without it the installed program has no map. Restore it from the repository."
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup).' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Write-Host 'Publishing the app (self-contained, win-x64)...'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if (-not (Test-Path (Join-Path $publish 'arcgis.keys.dat'))) { throw 'arcgis.keys.dat was not copied to the published app' }
# Only the scrambled keys go into the setup, never the readable local file
Remove-Item (Join-Path $publish 'arcgis.keys.json') -ErrorAction SilentlyContinue

Write-Host 'Building the setup...'
& $iscc "/DPublishDir=$publish" "/DAppVersion=$Version" (Join-Path $here 'ProjecteP1.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $here 'output\ProjecteP1-setup.exe'
Write-Host ("Done: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
