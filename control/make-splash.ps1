# control/make-splash.ps1 — render the native <SplashScreen> still (assets/splash.png).
# WPF shows this PNG instantly via unmanaged code BEFORE any JIT/extraction, so a self-contained
# single-file launch gets immediate feedback; it cross-fades into the animated BootWindow. A frozen
# frame of the boot: void field, arc-reactor crest, DOKICODE wordmark. Run: pwsh -File control\make-splash.ps1
param([string]$Out = (Join-Path $PSScriptRoot "assets\splash.png"))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

$W = 480; $H = 300
$cyan = [System.Drawing.Color]::FromArgb(255, 53, 224, 240)
$gold = [System.Drawing.Color]::FromArgb(255, 232, 199, 122)
$void = [System.Drawing.Color]::FromArgb(255, 5, 7, 11)

$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

# void field with a soft radial vignette toward the crest
$cx = 150.0; $cy = 150.0
$rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 11, 15, 22), $void, 0.0)
$g.FillRectangle($bg, $rect)

# --- the crest (reactor + gold hexagram), left-of-center, matching the boot ---
$S = 150.0
# gold magitek hexagram (hairline, no fill)
$penG = New-Object System.Drawing.Pen($gold, 1.0); $penG.Color = [System.Drawing.Color]::FromArgb(220, 232, 199, 122)
$hex = @(); for ($i = 0; $i -lt 6; $i++) { $a = ([math]::PI / 180) * (60 * $i - 90); $hex += New-Object System.Drawing.PointF([float]($cx + [math]::Cos($a) * 66), [float]($cy + [math]::Sin($a) * 66)) }
$g.DrawPolygon($penG, [System.Drawing.PointF[]]$hex)
$triA = @(); foreach ($k in 0, 2, 4) { $a = ([math]::PI / 180) * (60 * $k - 90); $triA += New-Object System.Drawing.PointF([float]($cx + [math]::Cos($a) * 60), [float]($cy + [math]::Sin($a) * 60)) }
$triB = @(); foreach ($k in 1, 3, 5) { $a = ([math]::PI / 180) * (60 * $k - 90); $triB += New-Object System.Drawing.PointF([float]($cx + [math]::Cos($a) * 60), [float]($cy + [math]::Sin($a) * 60)) }
$g.DrawPolygon($penG, [System.Drawing.PointF[]]$triA); $g.DrawPolygon($penG, [System.Drawing.PointF[]]$triB)
# cyan reactor rings
$penC1 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 53, 224, 240), 1.5)
$penC2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 53, 224, 240), 1.0)
$g.DrawEllipse($penC1, [float]($cx - 47), [float]($cy - 47), 94, 94)
$g.DrawEllipse($penC2, [float]($cx - 34), [float]($cy - 34), 68, 68)
# glowing cyan core
$gp = New-Object System.Drawing.Drawing2D.GraphicsPath; $gp.AddEllipse([float]($cx - 26), [float]($cy - 26), 52, 52)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(255, 207, 246, 255)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 53, 224, 240))
$g.FillPath($pgb, $gp)
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 53, 224, 240))), [float]($cx - 9), [float]($cy - 9), 18, 18)

# --- wordmark (right of the crest) ---
$fontD = New-Object System.Drawing.Font("Segoe UI Light", 26, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fontS = New-Object System.Drawing.Font("Cascadia Mono", 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$brT = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 234, 246, 255))
$brD = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 126, 140, 153))
$g.DrawString("D O K I C O D E", $fontD, $brT, 250, 122)
$g.DrawString("local intelligence stack", $fontS, $brD, 252, 158)

$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "wrote $Out ($([math]::Round((Get-Item $Out).Length/1KB,1)) KB, ${W}x${H})"
