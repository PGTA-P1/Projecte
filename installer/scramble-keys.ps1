# Writes Projecte1(WPF)\arcgis.keys.dat (committed) from Projecte1(WPF)\arcgis.keys.json (git-ignored, readable):
# the JSON's UTF-8 bytes XOR-ed with a fixed phrase, in base64, after the prefix "P1K1:" (ArcGisKeys.cs reads it).
# Use it after changing a key in arcgis.keys.json:  powershell -ExecutionPolicy Bypass -File installer\scramble-keys.ps1
# The .json format: { "License": "...", "ApiKey": "..." }

$ErrorActionPreference = 'Stop'
$folder = Join-Path (Split-Path $PSScriptRoot -Parent) 'Projecte1(WPF)'
$plain = Join-Path $folder 'arcgis.keys.json'
$scrambled = Join-Path $folder 'arcgis.keys.dat'
if (-not (Test-Path $plain)) { throw "Projecte1(WPF)\arcgis.keys.json not found: write the keys there first." }

$keys = Get-Content $plain -Raw | ConvertFrom-Json
if (-not $keys.ApiKey) { throw 'arcgis.keys.json has no ApiKey.' }

$bytes = [System.IO.File]::ReadAllBytes($plain)
$phrase = [System.Text.Encoding]::UTF8.GetBytes('PGTA-P1 trajectory entries and sector demand')
for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = $bytes[$i] -bxor $phrase[$i % $phrase.Length] }
[System.IO.File]::WriteAllText($scrambled, 'P1K1:' + [Convert]::ToBase64String($bytes), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Written: $scrambled (commit it; arcgis.keys.json stays local)"
