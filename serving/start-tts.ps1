# Start the TTS server — uncensored, local text-to-speech with voice cloning.
# Chatterbox (Resemble AI) behind the devnen Chatterbox-TTS-Server: an OpenAI-compatible
# /v1/audio/speech endpoint on :8004, plus /tts + /upload_reference for zero-shot voice
# cloning, and a web UI at http://127.0.0.1:8004/.
#
# Small (~4GB VRAM) so it COEXISTS with the coder LLM — no GPU-exclusive mode needed.
# The Perth watermark is stripped at install time, so output audio is genuinely unmarked.
# Runs in its own isolated cu128 venv (separate from llama.cpp / ComfyUI).
#
# Usage:  .\start-tts.ps1 [-Detach] [-PidFile <p>] [-LogFile <l>]
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$server = Join-Path (Split-Path $PSScriptRoot) "tts\Chatterbox-TTS-Server"
$py     = Join-Path $server ".venv\Scripts\python.exe"

if (-not (Test-Path $py)) {
    Write-Host "TTS server not installed. Run:  .\setup.ps1 -Tts"
    exit 1
}

if ($Detach) {
    $sp = @{ FilePath = $py; ArgumentList = @("server.py"); WorkingDirectory = $server; WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "TTS server starting in background on http://127.0.0.1:8004 (pid $($p.Id))"
} else {
    Set-Location $server
    & $py server.py
}
