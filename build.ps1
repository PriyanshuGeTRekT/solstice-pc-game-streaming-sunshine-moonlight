# Builds both apps and the release files into dist\.
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
#   dist\Solstice\                   the PC app, ready to run (client\SolsticePlay.exe inside)
#   dist\SolsticePlay.exe            the handheld app, one self-contained file
#   dist\Solstice-<version>.zip      PC app + installer, as attached to a GitHub release
#   dist\Solstice.zip                the same zip under a stable name for "latest" download links
param([switch]$NoZip)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

[xml]$proj = Get-Content (Join-Path $root 'src\Solstice\Solstice.csproj')
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "Building Solstice $version"
dotnet publish (Join-Path $root 'src\SolsticePlay\SolsticePlay.csproj') -c Release -o (Join-Path $dist 'play') -nologo
if ($LASTEXITCODE) { throw 'Solstice Play failed to build.' }
dotnet publish (Join-Path $root 'src\Solstice\Solstice.csproj') -c Release -o (Join-Path $dist 'Solstice') -nologo
if ($LASTEXITCODE) { throw 'Solstice failed to build.' }

Copy-Item (Join-Path $dist 'play\SolsticePlay.exe') (Join-Path $dist 'SolsticePlay.exe')
New-Item -ItemType Directory -Force (Join-Path $dist 'Solstice\client') | Out-Null
Copy-Item (Join-Path $dist 'play\SolsticePlay.exe') (Join-Path $dist 'Solstice\client\SolsticePlay.exe')
Remove-Item (Join-Path $dist 'play') -Recurse -Force
Get-ChildItem (Join-Path $dist 'Solstice') -Filter *.xml | Remove-Item

if ($NoZip) { return }

# Release layout: Solstice\Install Solstice.cmd, Solstice\install.ps1, Solstice\app\...
$stage = Join-Path $dist 'stage\Solstice'
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $dist 'Solstice') (Join-Path $stage 'app') -Recurse
Copy-Item (Join-Path $root 'tools\install.ps1') $stage
Set-Content -LiteralPath (Join-Path $stage 'Install Solstice.cmd') -Encoding ASCII -Value @'
@echo off
title Installing Solstice
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Source "%~dp0app" -Launch
if errorlevel 1 pause
'@

$zip = Join-Path $dist "Solstice-$version.zip"
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Copy-Item $zip (Join-Path $dist 'Solstice.zip')
Remove-Item (Join-Path $dist 'stage') -Recurse -Force

Get-ChildItem $dist -File | ForEach-Object { '{0,-24} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
