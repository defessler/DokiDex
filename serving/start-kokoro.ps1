# Start the Kokoro TTS server — the GATED, fast/light alternative to the :8004 Chatterbox default.
# Kokoro-82M (hexgrad, Apache-2.0) behind remsky/Kokoro-FastAPI: an OpenAI-compatible
# /v1/audio/speech endpoint on :8006, plus a web UI at http://127.0.0.1:8006/web.
#
# ADDITIVE — this does NOT replace Chatterbox (:8004, voice cloning, the coexisting-with-chat default).
# Kokoro is snappy + CPU-capable + <2GB VRAM (near-zero GPU contention) but has NO voice cloning
# (fixed preset voices), so it ships as a narration toggle, never the custom-voice default.
# Runs in its own isolated venv (separate from Chatterbox / llama.cpp / ComfyUI), loopback-bound.
#
# Usage:  .\start-kokoro.ps1 [-Detach] [-PidFile <p>] [-LogFile <l>]
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$server = Join-Path (Split-Path $PSScriptRoot) "kokoro\Kokoro-FastAPI"
$py     = Join-Path $server ".venv\Scripts\python.exe"

if (-not (Test-Path $py)) {
    Write-Host "Kokoro server not installed. Run:  .\setup.ps1 -Kokoro"
    exit 1
}

# Kokoro-FastAPI's `uvicorn api.src.main:app` entrypoint will NOT import or synthesize without the same env the
# upstream start-gpu.ps1 sets. Mirror it here (values are RELATIVE to $server / PROJECT_ROOT, the repo root):
#   PROJECT_ROOT + PYTHONPATH ($root;$root\api) — so `import api.src...` resolves (else ModuleNotFoundError)
#   MODEL_DIR / VOICES_DIR / WEB_PLAYER_PATH    — where the Kokoro-82M weights + the 54 preset voices + web UI live
#   PHONEMIZER_ESPEAK_LIBRARY                   — the espeak-ng DLL (setup.ps1 -Kokoro installs eSpeak-NG); without
#                                                 it phonemization throws at synth time
#   USE_GPU / USE_ONNX / PYTHONUTF8             — GPU torch path (cu128 venv), torch backend (not onnx), UTF-8 I/O
$env:PROJECT_ROOT             = $server
$env:PYTHONPATH               = "$server;$server\api"
$env:MODEL_DIR                = "src\models"
$env:VOICES_DIR               = "src\voices\v1_0"
$env:WEB_PLAYER_PATH          = "$server\web"
$env:PHONEMIZER_ESPEAK_LIBRARY = "C:\Program Files\eSpeak NG\libespeak-ng.dll"
$env:USE_GPU                  = "true"
$env:USE_ONNX                 = "false"
$env:PYTHONUTF8               = "1"

# Loopback-bind on :8006 (additive; the :8004 Chatterbox default is untouched). Kokoro-FastAPI is an
# ASGI app served via uvicorn — pin host/port here so the bind matches every sibling DokiDex server.
$argList = @("-m", "uvicorn", "api.src.main:app", "--host", "127.0.0.1", "--port", "8006")

if ($Detach) {
    $sp = @{ FilePath = $py; ArgumentList = $argList; WorkingDirectory = $server; WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "Kokoro server starting in background on http://127.0.0.1:8006 (pid $($p.Id))"
} else {
    Set-Location $server
    & $py @argList
}
