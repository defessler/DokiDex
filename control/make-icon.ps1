# control/make-icon.ps1 — generate the DokiDex arc-reactor app icon (assets/dokidex.ico).
# Pure GDI+: renders the reactor at several sizes and packs PNG frames into a multi-res .ico
# (Vista+ reads PNG-compressed frames). Also writes assets/dokidex-preview.png for inspection.
# Run:  pwsh -NoProfile -File control\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot "assets"))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$icoPath = Join-Path $OutDir "dokidex.ico"
$pngPath = Join-Path $OutDir "dokidex-preview.png"

$cyan = [System.Drawing.Color]::FromArgb(255, 53, 224, 240)    # #35E0F0 reactor
$gold = [System.Drawing.Color]::FromArgb(255, 232, 199, 122)   # #E8C77A

function New-Reactor([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $cx = $S / 2.0; $cy = $S / 2.0
    $detail = $S -ge 48    # ticks + core triangle only at larger sizes

    # outer ring (cyan) + gauge ticks
    $penO = New-Object System.Drawing.Pen($cyan, [float]([math]::Max(1.0, $S * 0.022)))
    $rO = $S * 0.44
    $g.DrawEllipse($penO, [float]($cx - $rO), [float]($cy - $rO), [float](2 * $rO), [float](2 * $rO))
    if ($detail) {
        $penT = New-Object System.Drawing.Pen($cyan, [float]($S * 0.012))
        for ($a = 0; $a -lt 360; $a += 30) {
            $rad = $a * [math]::PI / 180
            $x1 = $cx + [math]::Cos($rad) * ($rO - $S * 0.05); $y1 = $cy + [math]::Sin($rad) * ($rO - $S * 0.05)
            $x2 = $cx + [math]::Cos($rad) * ($rO - $S * 0.10); $y2 = $cy + [math]::Sin($rad) * ($rO - $S * 0.10)
            $g.DrawLine($penT, [float]$x1, [float]$y1, [float]$x2, [float]$y2)
        }
        $penT.Dispose()
    }

    # mid ring (gold) — segmented arcs for the magitek feel
    $penM = New-Object System.Drawing.Pen($gold, [float]([math]::Max(1.0, $S * 0.03)))
    $rM = $S * 0.32; $box = New-Object System.Drawing.RectangleF([float]($cx - $rM), [float]($cy - $rM), [float](2 * $rM), [float](2 * $rM))
    foreach ($start in 12, 102, 192, 282) { $g.DrawArc($penM, $box, [float]$start, [float]66) }

    # inner ring (cyan)
    $penI = New-Object System.Drawing.Pen($cyan, [float]([math]::Max(1.0, $S * 0.018)))
    $rI = $S * 0.22
    $g.DrawEllipse($penI, [float]($cx - $rI), [float]($cy - $rI), [float](2 * $rI), [float](2 * $rI))

    # glowing core (white -> cyan -> transparent radial)
    $rC = $S * 0.17
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddEllipse([float]($cx - $rC), [float]($cy - $rC), [float](2 * $rC), [float](2 * $rC))
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
    $pgb.CenterColor = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $cyan))
    $g.FillPath($pgb, $gp)

    # Iron Man core triangle
    if ($detail) {
        $penTri = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 255, 255, 255), [float]($S * 0.02))
        $rt = $S * 0.10; $pts = @()
        foreach ($a in -90, 30, 150) { $rad = $a * [math]::PI / 180; $pts += New-Object System.Drawing.PointF([float]($cx + [math]::Cos($rad) * $rt), [float]($cy + [math]::Sin($rad) * $rt)) }
        $g.DrawPolygon($penTri, [System.Drawing.PointF[]]$pts)
        $penTri.Dispose()
    }
    $penO.Dispose(); $penM.Dispose(); $penI.Dispose(); $gp.Dispose(); $pgb.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = 256, 64, 48, 32, 16
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Reactor $s
    if ($s -eq 256) { $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ size = $s; bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)   # ICONDIR
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.size -ge 256) { 0 } else { $f.size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$f.bytes.Length); $bw.Write([uint32]$offset)
    $offset += $f.bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Host "wrote $icoPath ($([math]::Round((Get-Item $icoPath).Length/1KB,1)) KB, $($frames.Count) frames) + preview"
