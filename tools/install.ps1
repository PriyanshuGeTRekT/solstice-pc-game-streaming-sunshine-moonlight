# Installs Solstice for the current user: no administrator rights needed.
#   -Source       folder with Solstice.exe (and client\SolsticePlay.exe); defaults to .\app next to this script
#   -Launch       open Solstice when done
#   -MigrateFrom  optional older data folder whose settings and saved login should be carried over
#   -Log          optional file to write progress to
param(
    [string]$Source = (Join-Path $PSScriptRoot 'app'),
    [switch]$Launch,
    [string]$MigrateFrom,
    [string]$Log
)
$ErrorActionPreference = 'Stop'
function Say($m) { if ($Log) { Add-Content -LiteralPath $Log -Value $m -Encoding UTF8 } else { Write-Host $m } }

try {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\Solstice'
    $data = Join-Path $env:LOCALAPPDATA 'Solstice'
    $exe = Join-Path $target 'Solstice.exe'

    if (-not (Test-Path (Join-Path $Source 'Solstice.exe'))) { throw "No Solstice.exe in $Source" }

    Get-Process Solstice -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    Say "Copying app to $target"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $Source '*') $target -Recurse -Force

    if ($MigrateFrom -and (Test-Path $MigrateFrom)) {
        New-Item -ItemType Directory -Force $data | Out-Null
        foreach ($f in 'settings.json', 'sunshine-login.bin') {
            $from = Join-Path $MigrateFrom $f
            $to = Join-Path $data $f
            if ((Test-Path $from) -and -not (Test-Path $to)) { Copy-Item $from $to; Say "Carried over $f" }
        }
    }

    # Uninstaller (leaves Sunshine, the controller driver and paired devices alone).
    $uninstall = @'
$ErrorActionPreference = 'SilentlyContinue'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Solstice'
Get-Process Solstice | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Solstice.lnk') -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Solstice.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Solstice' -Recurse -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'Solstice') -Recurse -Force
Start-Process cmd.exe -ArgumentList '/c', "timeout /t 2 >nul & rmdir /s /q `"$target`"" -WindowStyle Hidden
'@
    Set-Content -LiteralPath (Join-Path $target 'uninstall.ps1') -Value $uninstall -Encoding UTF8

    Say 'Adding shortcuts'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory')) {
        $lnk = $shell.CreateShortcut((Join-Path $folder 'Solstice.lnk'))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $target
        $lnk.Description = 'Stream games from this PC to your handheld'
        $lnk.IconLocation = "$exe,0"
        $lnk.Save()
    }

    Say 'Registering in Installed apps'
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Solstice'
    New-Item -Path $key -Force | Out-Null
    $size = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $values = @{
        DisplayName = 'Solstice'
        DisplayVersion = (Get-Item $exe).VersionInfo.ProductVersion
        Publisher = 'Solstice'
        InstallLocation = $target
        DisplayIcon = "$exe,0"
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target\uninstall.ps1`""
    }
    foreach ($k in $values.Keys) { Set-ItemProperty -Path $key -Name $k -Value $values[$k] }
    Set-ItemProperty -Path $key -Name EstimatedSize -Value $size -Type DWord
    Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord

    Say 'INSTALLED'
    if ($Launch) { Start-Process -FilePath $exe -WorkingDirectory $target }
} catch {
    Say "FAILED: $($_.Exception.Message)"
    exit 1
}
