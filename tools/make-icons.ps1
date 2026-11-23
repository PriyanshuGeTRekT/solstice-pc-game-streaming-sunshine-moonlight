# Draws the two app icons (PC app: sun with a moon's bite; handheld app: sun with a play mark)
# and writes them as multi-size .ico files with PNG-compressed entries.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

Add-Type -AssemblyName System.Drawing

function New-Mark([int]$size, [string]$kind) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded dark tile
    $r = [single]($size * 0.22)
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $tile = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 19, 22, 30))
    $g.FillPath($tile, $path)

    # Sun disc
    $pad = [single]($size * 0.2)
    $disc = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $disc, ([System.Drawing.Color]::FromArgb(255, 255, 196, 92)), ([System.Drawing.Color]::FromArgb(255, 255, 128, 54)), 55
    $g.FillEllipse($grad, $disc)

    if ($kind -eq 'host') {
        # Moon's bite, upper right
        $bite = [single]($size * 0.36)
        $g.FillEllipse($tile, [single]($size * 0.5), [single]($size * 0.14), $bite, $bite)
    } else {
        # Play triangle
        $cx = $size * 0.52; $cy = $size * 0.5; $t = $size * 0.15
        $pts = @(
            (New-Object System.Drawing.PointF ([single]($cx - $t * 0.75)), ([single]($cy - $t))),
            (New-Object System.Drawing.PointF ([single]($cx + $t)), ([single]$cy)),
            (New-Object System.Drawing.PointF ([single]($cx - $t * 0.75)), ([single]($cy + $t)))
        )
        $g.FillPolygon($tile, [System.Drawing.PointF[]]$pts)
    }
    $g.Dispose()
    return $bmp
}

function Write-Ico([string]$file, [string]$kind) {
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $images = foreach ($s in $sizes) {
        $bmp = New-Mark $s $kind
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    $fs = [System.IO.File]::Create($file)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $bw.Write($img) }
    $bw.Dispose(); $fs.Dispose()
    "wrote $file"
}

Write-Ico (Join-Path $Root 'src\Solstice\Assets\app.ico') 'host'
Write-Ico (Join-Path $Root 'src\SolsticePlay\Assets\app.ico') 'play'

# PNG previews for the UI
(New-Mark 96 'host').Save((Join-Path $Root 'src\Solstice\wwwroot\mark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(New-Mark 96 'play').Save((Join-Path $Root 'src\SolsticePlay\wwwroot\mark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
