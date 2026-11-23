# Captures every demo screen of both apps at an exact viewport through Edge's DevTools protocol
# (headless), for the YouTube video. Nothing real is read: the apps run with --demo.
param([string]$Out = (Join-Path $PSScriptRoot 'frames'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$hostExe = "$root\src\Solstice\bin\Debug\net8.0-windows\win-x64\Solstice.exe"
$playExe = "$root\src\SolsticePlay\bin\Debug\net8.0-windows\win-x64\SolsticePlay.exe"
New-Item -ItemType Directory -Force $Out | Out-Null

$profileDir = Join-Path ([IO.Path]::GetTempPath()) 'solstice-cdp-profile'
$edgeProc = Start-Process -FilePath $edge -PassThru -WindowStyle Hidden -ArgumentList @(
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--remote-debugging-port=9333',
    "--user-data-dir=`"$profileDir`"", 'about:blank')
for ($i = 0; $i -lt 40; $i++) { try { Invoke-RestMethod 'http://127.0.0.1:9333/json/version' -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 250 } }

$script:msgId = 0
function Invoke-Cdp($ws, [string]$method, $params) {
    $script:msgId++
    $id = $script:msgId
    $json = (@{ id = $id; method = $method; params = $(if ($params) { $params } else { @{} }) } | ConvertTo-Json -Depth 8 -Compress)
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).Wait()
    $buf = New-Object byte[] (1MB)
    while ($true) {
        $ms = New-Object IO.MemoryStream
        do {
            $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result
            $ms.Write($buf, 0, $r.Count)
        } while (-not $r.EndOfMessage)
        $msg = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
        if ($msg.id -eq $id) { if ($msg.error) { throw "$method failed: $($msg.error.message)" }; return $msg.result }
    }
}

function Capture($name, $exe, $scene, $hash, $w, $h, $port) {
    $p = Start-Process -FilePath $exe -ArgumentList @('--serve', '--demo', $scene, '--port', $port, '--token', 'v') -PassThru -WindowStyle Hidden
    try {
        for ($t = 0; $t -lt 40; $t++) { try { Invoke-WebRequest "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { if ($_.Exception.Response) { break }; Start-Sleep -Milliseconds 250 } }
        $target = Invoke-RestMethod -Method Put 'http://127.0.0.1:9333/json/new?about:blank'
        $ws = New-Object Net.WebSockets.ClientWebSocket
        $ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
        try {
            Invoke-Cdp $ws 'Emulation.setDeviceMetricsOverride' @{ width = $w; height = $h; deviceScaleFactor = 1.5; mobile = $false } | Out-Null
            Invoke-Cdp $ws 'Emulation.setEmulatedMedia' @{ features = @(@{ name = 'prefers-reduced-motion'; value = 'reduce' }) } | Out-Null
            Invoke-Cdp $ws 'Page.navigate' @{ url = "http://127.0.0.1:$port/?t=v$hash" } | Out-Null
            Start-Sleep -Milliseconds 3500
            $shot = Invoke-Cdp $ws 'Page.captureScreenshot' @{ format = 'png'; captureBeyondViewport = $false }
            [IO.File]::WriteAllBytes((Join-Path $Out "$name.png"), [Convert]::FromBase64String($shot.data))
            "captured $name"
        } finally {
            $ws.Dispose()
            Invoke-RestMethod "http://127.0.0.1:9333/json/close/$($target.id)" | Out-Null
        }
    } finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}

try {
    # PC app at 1440x900 CSS px, handheld at 1280x720 (the ROG Ally's 1080p screen at 150 %); both captured at 1.5x.
    $shots = @(
        @('pc-welcome', $hostExe, 'welcome', '', 1440, 900), @('pc-home', $hostExe, 'home', '', 1440, 900),
        @('pc-streaming', $hostExe, 'streaming', '', 1440, 900), @('pc-link', $hostExe, 'link', '#devices', 1440, 900),
        @('pc-approve', $hostExe, 'approve', '#devices', 1440, 900), @('pc-library', $hostExe, 'library', '#library', 1440, 900),
        @('pc-tuning', $hostExe, 'tuning', '#tuning', 1440, 900),
        @('ally-welcome', $playExe, 'welcome', '', 1280, 720), @('ally-setup', $playExe, 'setup', '', 1280, 720),
        @('ally-pair', $playExe, 'pair', '', 1280, 720), @('ally-home', $playExe, 'home', '', 1280, 720),
        @('ally-settings', $playExe, 'home', '#settings', 1280, 720), @('ally-asleep', $playExe, 'asleep', '', 1280, 720),
        @('ally-playing', $playExe, 'playing', '', 1280, 720)
    )
    for ($i = 0; $i -le 6; $i++) { $shots += , @("pc-setup-$i", $hostExe, "setup-$i", '', 1440, 900) }
    $port = 47950
    foreach ($s in $shots) { $port++; Capture $s[0] $s[1] $s[2] $s[3] $s[4] $s[5] $port }
} finally {
    Stop-Process -Id $edgeProc.Id -Force -ErrorAction SilentlyContinue
}
