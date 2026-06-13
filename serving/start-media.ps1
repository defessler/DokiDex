# Start SwarmUI — local image + video generation on :7801.
# SwarmUI drives the ComfyUI backend it provisions. Filter-free. Installed by
# ..\setup.ps1 -Media (clones to media\SwarmUI and downloads models).
#
# Usage:  .\start-media.ps1 [-Detach] [-PidFile <p>] [-LogFile <l>]
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$root   = Split-Path $PSScriptRoot
$swarm  = Join-Path $root "media\SwarmUI"
$launch = Join-Path $swarm "launch-windows.bat"

if (-not (Test-Path $launch)) {
    Write-Host "SwarmUI is not installed yet. Run:  .\setup.ps1 -Media"
    exit 1
}

if ($Detach) {
    $sp = @{ FilePath = $launch; WorkingDirectory = $swarm; WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "SwarmUI starting on http://127.0.0.1:7801 (pid $($p.Id))"
} else {
    & $launch
}
