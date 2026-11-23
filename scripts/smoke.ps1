# Smoke test: starts both apps headless in demo mode and checks that every screen's data,
# the UI files and the access controls answer as expected. Safe to run anywhere (CI included):
# demo mode never touches Sunshine, Moonlight, the network or the real game library.
#   powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1 [-Config Debug|Release]
param([string]$Config = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failures = 0

function Check($label, [scriptblock]$test) {
    try {
        if (& $test) { Write-Host "  ok    $label" } else { Write-Host "  FAIL  $label"; $script:failures++ }
    } catch {
        Write-Host "  FAIL  $label ($($_.Exception.Message))"; $script:failures++
    }
}

function Get-Json($port, $path, $token = 'smoke') {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $s.Cookies.Add((New-Object System.Net.Cookie('solstice', $token, '/', '127.0.0.1')))
    (Invoke-WebRequest "http://127.0.0.1:$port$path" -WebSession $s -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
}

function Get-Status($port, $path, $cookie) {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    if ($cookie) { $s.Cookies.Add((New-Object System.Net.Cookie('solstice', $cookie, '/', '127.0.0.1'))) }
    try { (Invoke-WebRequest "http://127.0.0.1:$port$path" -WebSession $s -UseBasicParsing -TimeoutSec 10).StatusCode }
    catch { [int]$_.Exception.Response.StatusCode }
}

function Start-App($exe, $scene, $port) {
    $p = Start-Process -FilePath $exe -ArgumentList @('--serve', '--demo', $scene, '--port', $port, '--token', 'smoke') -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) {
        if ((Get-Status $port '/' $null) -gt 0) { return $p }
        Start-Sleep -Milliseconds 250
    }
    throw "$exe did not start"
}

$hostExe = Join-Path $root "src\Solstice\bin\$Config\net8.0-windows\win-x64\Solstice.exe"
$playExe = Join-Path $root "src\SolsticePlay\bin\$Config\net8.0-windows\win-x64\SolsticePlay.exe"

Write-Host 'Solstice (PC app)'
$p = Start-App $hostExe 'home' 47811
try {
    Check 'state reports ready' { (Get-Json 47811 '/api/state').ready -eq $true }
    Check 'seven health checks' { @((Get-Json 47811 '/api/state').checks).Count -eq 7 }
    Check 'library lists launchers and games' { @((Get-Json 47811 '/api/library').items).Count -ge 10 }
    Check 'devices lists paired devices' { @((Get-Json 47811 '/api/devices').clients).Count -eq 2 }
    Check 'box art is a PNG' {
        $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $s.Cookies.Add((New-Object System.Net.Cookie('solstice', 'smoke', '/', '127.0.0.1')))
        $r = Invoke-WebRequest 'http://127.0.0.1:47811/file/cover/launcher%3Asteam' -WebSession $s -UseBasicParsing -TimeoutSec 20
        $r.Headers['Content-Type'] -eq 'image/png' -and $r.RawContentLength -gt 1000
    }
    Check 'UI files are served' { (Get-Status 47811 '/app.js' 'smoke') -eq 200 -and (Get-Status 47811 '/app.css' 'smoke') -eq 200 }
    Check 'requests without the session cookie are refused' { (Get-Status 47811 '/api/state' $null) -eq 403 }
    Check 'a wrong session cookie is refused' { (Get-Status 47811 '/api/state' 'wrong') -eq 403 }
    Check 'path traversal is refused' { (Get-Status 47811 '/..%2f..%2fwindows%2fwin.ini' 'smoke') -in 400..404 }
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

$p = Start-App $hostExe 'setup-3' 47812
try {
    Check 'setup scene shows progress' {
        $st = (Get-Json 47812 '/api/state').setup
        $st.running -and @($st.steps | Where-Object state -eq 'done').Count -eq 3
    }
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

Write-Host 'Solstice Play (handheld app)'
$p = Start-App $playExe 'home' 47813
try {
    Check 'state reports a paired PC' { (Get-Json 47813 '/api/state').host.name -eq 'GAMING-PC' }
    Check 'stream fits the screen' { $s = (Get-Json 47813 '/api/state').stream; $s.width -eq 1920 -and $s.fps -eq 120 }
    Check 'apps are listed' { @((Get-Json 47813 '/api/apps').apps).Count -ge 5 }
    Check 'UI files are served' { (Get-Status 47813 '/app.js' 'smoke') -eq 200 }
    Check 'requests without the session cookie are refused' { (Get-Status 47813 '/api/state' $null) -eq 403 }
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

if ($failures) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host 'All checks passed'
