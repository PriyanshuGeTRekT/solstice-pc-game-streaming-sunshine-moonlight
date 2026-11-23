# Renders every screen of both apps from simulated data (--demo), for docs/screenshots.
# Uses headless Microsoft Edge, so no window opens and nothing real is read or changed.
#   powershell -ExecutionPolicy Bypass -File tools\render-screens.ps1 [-Out docs\screenshots] [-Config Debug]
param(
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\screenshots'),
    [string]$Config = 'Debug',
    [double]$Scale = 2
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is needed to render screenshots.' }

$host_ = Join-Path $root "src\Solstice\bin\$Config\net8.0-windows\win-x64\Solstice.exe"
$play = Join-Path $root "src\SolsticePlay\bin\$Config\net8.0-windows\win-x64\SolsticePlay.exe"
foreach ($exe in $host_, $play) { if (-not (Test-Path $exe)) { throw "Build first: $exe is missing." } }

New-Item -ItemType Directory -Force $Out | Out-Null
$profileDir = Join-Path ([IO.Path]::GetTempPath()) 'solstice-render-profile'

# name, exe, scene, hash, width, height
$shots = @(
    @('pc-welcome',   $host_, 'welcome',   '',          1180, 780),
    @('pc-setup',     $host_, 'setup-4',   '',          1180, 780),
    @('pc-home',      $host_, 'home',      '',          1180, 780),
    @('pc-streaming', $host_, 'streaming', '',          1180, 780),
    @('pc-approve',   $host_, 'approve',   '#devices',  1180, 780),
    @('pc-library',   $host_, 'library',   '#library',  1180, 780),
    @('pc-tuning',    $host_, 'tuning',    '#tuning',   1180, 780),
    @('ally-welcome', $play,  'welcome',   '',          1280, 720),
    @('ally-pair',    $play,  'pair',      '',          1280, 720),
    @('ally-home',    $play,  'home',      '',          1280, 720),
    @('ally-settings',$play,  'home',      '#settings', 1280, 720),
    @('ally-asleep',  $play,  'asleep',    '',          1280, 720),
    @('ally-playing', $play,  'playing',   '',          1280, 720)
)
# Frames for the demo GIF: the PC setup, then pairing the handheld.
for ($i = 0; $i -le 6; $i++) { $shots += , @("frame-pc-setup-$i", $host_, "setup-$i", '', 1180, 780) }

$port = 47700
foreach ($s in $shots) {
    $name, $exe, $scene, $hash, $w, $h = $s
    $port++
    $token = 'render'
    $p = Start-Process -FilePath $exe -ArgumentList @('--serve', '--demo', $scene, '--port', $port, '--token', $token) -PassThru -WindowStyle Hidden
    try {
        $url = "http://127.0.0.1:$port/?t=$token$hash"
        for ($t = 0; $t -lt 40; $t++) {
            try { Invoke-WebRequest "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { if ($_.Exception.Response) { break }; Start-Sleep -Milliseconds 250 }
        }
        $file = Join-Path $Out "$name.png"
        Remove-Item $file -ErrorAction SilentlyContinue
        $edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--force-prefers-reduced-motion',
            "--user-data-dir=`"$profileDir`"", "--force-device-scale-factor=$Scale", "--window-size=$w,$h",
            '--virtual-time-budget=8000', "--screenshot=`"$file`"", "`"$url`"")
        $log = Join-Path ([IO.Path]::GetTempPath()) 'solstice-render-edge.log'
        Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden -RedirectStandardError $log | Out-Null
        if (Test-Path $file) { "rendered $name" } else { "FAILED $name" }
    } finally {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
}
