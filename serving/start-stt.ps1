# Start the STT server — fully-local speech-to-text (NVIDIA Parakeet via onnx-asr).
# OpenAI-compatible /v1/audio/transcriptions on :8005. CPU EP by default (~no VRAM),
# so it COEXISTS with the coder LLM in agent mode. Runs in its own isolated venv.
#
# Usage:  .\start-stt.ps1 [-Detach] [-PidFile <p>] [-LogFile <l>]
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$sttRoot = Join-Path (Split-Path $PSScriptRoot) "stt"
$py      = Join-Path $sttRoot ".venv\Scripts\python.exe"
$server  = Join-Path $PSScriptRoot "stt-server.py"

if (-not (Test-Path $py)) {
    Write-Host "STT server not installed. Run:  .\setup.ps1 -Stt"
    exit 1
}

if ($Detach) {
    $sp = @{ FilePath = $py; ArgumentList = @("`"$server`""); WorkingDirectory = $sttRoot; WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "STT server starting in background on http://127.0.0.1:8005 (pid $($p.Id))"
} else {
    Set-Location $sttRoot
    & $py "$server"
}
