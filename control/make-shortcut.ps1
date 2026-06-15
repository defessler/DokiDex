# control/make-shortcut.ps1 — create/refresh DokiDex.lnk: the premium, console-free entry point.
# A real Windows shortcut straight to the WinExe (no cmd flash) carrying the arc-reactor icon.
# Created by `doki panel` / setup.ps1 after the app is built. Optional -Desktop / -StartMenu.
param([switch]$Desktop, [switch]$StartMenu)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot                       # repo root (control/..)
$exe  = Join-Path $repo "control\bin\Release\net9.0-windows\DokiDex.Control.exe"
$ico  = Join-Path $repo "control\assets\dokidex.ico"
if (-not (Test-Path $exe)) { Write-Host "  !!  exe not built yet — build first (doki panel / setup.ps1)"; return }

function New-Lnk($path) {
    $sh = New-Object -ComObject WScript.Shell
    $lnk = $sh.CreateShortcut($path)
    $lnk.TargetPath       = $exe
    $lnk.WorkingDirectory = Split-Path $exe
    $lnk.Description       = "DokiDex — local AI engine"
    if (Test-Path $ico) { $lnk.IconLocation = "$ico,0" }   # else inherits the exe's embedded icon
    $lnk.Save()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($sh) | Out-Null
    Write-Host "  ok  $path"
}

# primary: a launcher in the repo root the user double-clicks instead of control.bat
New-Lnk (Join-Path $repo "DokiDex.lnk")
if ($Desktop)   { New-Lnk (Join-Path ([Environment]::GetFolderPath('Desktop')) "DokiDex.lnk") }
if ($StartMenu) {
    $sm = Join-Path ([Environment]::GetFolderPath('Programs')) "DokiDex"
    New-Item -ItemType Directory -Force $sm | Out-Null
    New-Lnk (Join-Path $sm "DokiDex.lnk")
}
