# setup.ps1 — one-command DokiDex bootstrap (native, no Docker). Idempotent.
#
# Verifies prereqs, deploys configs, and with -Media installs the fully-local,
# uncensored image + video generation stack (SwarmUI + ComfyUI + models),
# completely headlessly (no GUI install wizard).
#
# Usage:
#   .\setup.ps1                       core: prereqs + config deploy (LLM/chat/code)
#   .\setup.ps1 -Media                + SwarmUI/ComfyUI + the verified uncensored models
#   .\setup.ps1 -Media -Models full   + ~100-115GB quality kit (Wan 2.2 14B/5B, Qwen-Image-Edit, ACE-Step music, LTXV, Foley, 4x upscale, Z-Image Base, Chroma, Illustrious-XL + Animagine XL anime SDXL, :8013 rewriter)
#
# Then:  .\doki.ps1 up        (chat + code)        .\doki.ps1 up media   (image + video)
param(
    [switch]$Media,
    [switch]$Tts,
    [switch]$Stt,
    [switch]$Demucs,    # optional audio-tools sidecar: stem separation (vocals/drums/bass/other) via Demucs
    [switch]$Sam,       # optional sidecar: Segment-Anything point segmentation (semantic click->mask in the edit canvas)
    [switch]$Train,     # optional sidecar: in-app LoRA training (kohya sd-scripts)
    [switch]$FaceId,    # optional sidecar: InstantID face-identity reference (SDXL — reuses the anime Illustrious/Animagine base, no FLUX needed)
    [switch]$Pulid,     # optional sidecar: PuLID-Flux face-identity (FLUX.1-dev) — pulls a NON-GATED ~17GB FLUX fp8 base (Kijai unet + t5xxl/clip_l/ae) + the balazik PuLID-Flux node (Alpha/stale); SHARES InstantID's antelopev2
    [switch]$InfiniteTalk,  # optional sidecar: audio-driven talking-video via MeiGen InfiniteTalk on the Wan2.1-I2V-14B base — NOTE pulls an ~82GB Wan2.1 base NOT otherwise on disk
    [switch]$LatentSync,    # optional sidecar: the LIGHT lip-sync — ByteDance LatentSync 1.5 (video-in mouth re-sync to -Audio), ~9.5GB on disk / 8GB VRAM (fits 32GB with huge headroom; OpenRAIL++ weights / Apache code) — the LIGHTER alternative to the ~82GB InfiniteTalk (a DIFFERENT job: re-sync vs portrait->video, so ADDITIVE not a replacement)
    [switch]$TtsSuite,  # optional sidecar: TTS-Audio-Suite ComfyUI node (15 TTS engines + RVC). A GATED ALTERNATIVE to the standalone :8004 Chatterbox server (which stays the coexisting-with-chat default, untouched) — this one runs in the GPU-exclusive media group. Engines AUTO-DOWNLOAD their own weights on first use (nothing pre-fetched); the runtime workflow is the on-GPU authoring step.
    [switch]$Kokoro,    # optional sidecar: Kokoro-82M (hexgrad, Apache-2.0) via remsky/Kokoro-FastAPI — a GATED, ADDITIVE fast/light TTS alternative on :8006 (own venv, loopback-bound). Snappy, CPU-capable, <2GB VRAM, near-zero GPU contention — but NO voice cloning (fixed preset voices), so it is a narration TOGGLE, never the default. Does NOT touch the coexisting-with-chat :8004 Chatterbox default.
    [switch]$Nunchaku,  # optional sidecar: Nunchaku NVFP4 speed runtime (wheel + ComfyUI-nunchaku node) — ~3x faster on Blackwell/RTX-50xx for the Z-Image-Turbo (the default base) + Qwen-Image NVFP4 svdq variants. +Models full fetches them. (FLUX.2 Klein NVFP4 is BFL-native FP4, fetched under -Models full, not here.)
    [switch]$Vision,    # optional: vision model (Qwen3-VL-8B) -> lights up the studio Describe/Verify surfaces
    [switch]$LlmCandidates,  # optional: download the coder/heavy bake-off candidates (Qwen3.6 / Qwen3-Coder-Next) for eval
    [switch]$Ocr,       # optional sidecar: scanned/image-PDF OCR for the KB ("chat with your documents"). Installs Tesseract (UB-Mannheim) + the lazy pip parsers (pymupdf/pytesseract/Pillow) on the doc_ingest_bin uv overlay. Off by default; a text PDF never touches it. NO GPU, no always-on server.
    [switch]$Managed,   # invoked by the all-in-one app: the panel IS this self-contained exe (baked in),
                        # so don't install the .NET SDK to rebuild it — only -Media needs the SDK (SwarmUI).
    [ValidateSet("lean", "full")][string]$Models = "lean"
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$root = $PSScriptRoot

function Info($m) { Write-Host "[setup] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  ok  $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  !!  $m" -ForegroundColor Yellow }
function Sync-Path {
    # Refresh THIS session's PATH from the machine+user registry so a tool winget just
    # installed (python/git/dotnet) resolves later in the same run — winget edits the
    # registry, not our already-started process environment.
    $parts = @([Environment]::GetEnvironmentVariable('Path','Machine'),
               [Environment]::GetEnvironmentVariable('Path','User')) | Where-Object { $_ }
    $env:Path = $parts -join ';'
}
function Ensure-WinGet($id, $cmd) {
    if ($cmd -and (Get-Command $cmd -ErrorAction SilentlyContinue)) { Ok "$cmd present"; return }
    # winget itself is the one prerequisite we can't install — fail loud with guidance (a fresh/LTSC
    # Windows without App Installer otherwise throws a cryptic CommandNotFoundException here).
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget (App Installer) is required — install 'App Installer' from the Microsoft Store (https://aka.ms/getwinget), then re-run setup.ps1"
    }
    Info "installing $id ..."
    winget install $id --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
    $code = $LASTEXITCODE
    Sync-Path   # so a freshly-installed command resolves later in this same session
    if ($cmd) {
        # the real verification: did the command land on PATH? (catches a silently-failed install)
        if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "winget install $id failed (exit $code): '$cmd' still not on PATH" }
        Ok "$cmd installed"
    } elseif ($code -ne 0 -and $code -ne -1978335189) {   # -1978335189 = 0x8A15002B (no applicable update / already current) is benign
        Warn "winget install $id returned exit $code (continuing; verify the app installed)"
    }
}
function Pip($py) {
    # Run pip in the given venv and FAIL LOUD: line 20 disables native-command error
    # propagation, so each dependency step must check $LASTEXITCODE explicitly or a broken
    # install passes silently and later gets cached as "present".
    & $py -m pip @args
    if ($LASTEXITCODE -ne 0) { throw "pip failed (exit $LASTEXITCODE): pip $($args -join ' ')" }
}
function Git-Clone($url, $dest) {
    # Clone and FAIL LOUD, removing a partial dir on failure — a hard kill mid-clone otherwise leaves
    # a partial checkout the existence-only gates treat as "present" and never retry (same discipline
    # as the .part-then-Move model downloads). Line 20 mutes native errors, so check $LASTEXITCODE.
    git clone $url $dest
    if ($LASTEXITCODE -ne 0) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue; throw "git clone failed (exit $LASTEXITCODE): $url" }
}
function Ensure-DotNet9 {
    # The single SDK for the whole stack: builds the WPF panel (dev) AND SwarmUI's net8.0. Probe via
    # Get-Command (a bare `dotnet` throws CommandNotFoundException that 2>$null can't suppress when absent).
    $hasNet9 = $false
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { $hasNet9 = [bool]((dotnet --list-sdks 2>$null) -match '9\.0\.') }
    if (-not $hasNet9) { Ensure-WinGet "Microsoft.DotNet.SDK.9" $null } else { Ok ".NET 9 SDK present" }
}

# ---- 1. Preflight ---------------------------------------------------------
Info "preflight"
try { Ok "GPU: $((nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader) -join '')" }
catch { Warn "nvidia-smi not found — an NVIDIA GPU + driver is required" }
Ok "free disk on $($root.Substring(0,2)) $([math]::Round((Get-PSDrive $root.Substring(0,1)).Free/1GB,0)) GB"

# ---- 2. Host tools (the things you launch yourself: CLI + chat) -----------
Info "host tools"
Ensure-WinGet "charmbracelet.crush" "crush"   # code CLI
Ensure-WinGet "Bin-Huang.Chatbox" $null        # chat app (GUI, no CLI command)
Ensure-WinGet "astral-sh.uv" "uv"              # runs the DuckDuckGo search MCP

# ---- 3. Deploy configs ----------------------------------------------------
Info "deploy configs"
$crushDst = Join-Path $env:USERPROFILE ".config\crush"
New-Item -ItemType Directory -Force $crushDst | Out-Null
# Rewrite the memory MCP's server.py path to THIS install root before deploying. crush runs from
# ~\.config\crush (an arbitrary CWD), so the path must be absolute + correct even after a project
# move — patch it here rather than trusting the committed value.
$crushJson = (Get-Content (Join-Path $root "harness\crush.json") -Raw) -replace '"[^"]*?/serving/memory-mcp/server\.py"', ('"' + ($root -replace '\\', '/') + '/serving/memory-mcp/server.py"')
Set-Content (Join-Path $crushDst "crush.json") $crushJson
Ok "crush.json -> $crushDst  (memory MCP path pinned to $root; deps install on first launch via uv)"
# Seed the persistent-memory store with DokiDex's facts/gotchas (idempotent; needs python).
if (Get-Command python -ErrorAction SilentlyContinue) {
    try { python (Join-Path $root "serving\memory-mcp\seed.py") | Out-Null; Ok "memory store seeded (serving\memory-mcp)" } catch { Warn "memory seed skipped ($($_.Exception.Message))" }
}
if (Test-Path (Join-Path $env:APPDATA "Code\User")) {
    Ok "VS Code found — merge harness\llama.vscode-settings.json into Code\User\settings.json (it's a partial)"
} else { Warn "VS Code not found; skip llama.vscode autocomplete settings" }
Warn "Chatbox endpoint is a 20-sec GUI step: Settings -> add OpenAI-compatible provider,"
Warn "   API Host http://127.0.0.1:8080/v1 , key 'dummy' , models coder-fast / coder-big"

# ---- 4. LLM assets (verify present; these are large, fetched out of band) --
Info "LLM assets"
@{
    "serving\llama-swap\llama-swap.exe"  = "llama-swap release (github.com/mostlygeek/llama-swap)"
    "serving\llama.cpp\llama-server.exe" = "llama.cpp CUDA build (github.com/ggml-org/llama.cpp)"
    "serving\llama-swap.yaml"            = "model router config (in repo)"
    "models\qwen2.5-coder-3b-q8_0.gguf"  = "FIM model (HF: Qwen2.5-Coder-3B GGUF Q8)"
}.GetEnumerator() | ForEach-Object {
    if (Test-Path (Join-Path $root $_.Key)) { Ok $_.Key } else { Warn "MISSING $($_.Key) -> $($_.Value)" }
}

# Embed model for the codebase RAG (the code_search MCP tool; CPU-served on :8090). Small (~260MB) and
# reliable, so — unlike the large out-of-band coders above — it's auto-fetched here for turnkey RAG.
$embedGguf = Join-Path $root "models\nomic-embed-text-v1.5.f16.gguf"
if (Test-Path $embedGguf) { Ok "embed model present (nomic-embed-text-v1.5)" }
else {
    Info "downloading embed model (nomic-embed-text-v1.5 f16, ~260MB) for code_search ..."
    New-Item -ItemType Directory -Force (Split-Path $embedGguf) | Out-Null
    curl.exe -L --fail --retry 3 -o "$embedGguf.part" "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/main/nomic-embed-text-v1.5.f16.gguf"
    if ($LASTEXITCODE -eq 0) { Move-Item -Force "$embedGguf.part" $embedGguf; Ok "embed model downloaded" }
    else { Remove-Item "$embedGguf.part" -Force -ErrorAction SilentlyContinue; Warn "embed model download failed — code_search stays off until it's present" }
}

# Self-contained GGUF fetcher (Get-Model is defined later, in the media section; this works anywhere).
function Fetch-Gguf($url, $dest) {
    if (Test-Path $dest) { Ok "have $(Split-Path $dest -Leaf)"; return }
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Info "downloading $(Split-Path $dest -Leaf) ..."
    curl.exe -L --fail --retry 3 -o "$dest.part" $url
    if ($LASTEXITCODE -eq 0) { Move-Item -Force "$dest.part" $dest; Ok "downloaded $(Split-Path $dest -Leaf)" }
    else { Remove-Item "$dest.part" -Force -ErrorAction SilentlyContinue; Warn "download failed: $url" }
}

# ---- Vision model (optional): lights up the studio's Describe (image->prompt) + Verify (output-QA) ----
# Served by llama-swap as the "vision" model on :8080 (the in-app code already targets it). Use INSTRUCT, not
# Thinking. If Qwen3-VL CLIP fails to load on this llama.cpp build, drop an abliterated Qwen2.5-VL GGUF+mmproj
# into models\ with the same filenames (uncensored fallback) and/or bump llama.cpp.
if ($Vision) {
    Info "Vision model (Qwen3-VL-8B-Instruct + mmproj, ~7GB)"
    $mdir = Join-Path $root "models"
    Fetch-Gguf "https://huggingface.co/unsloth/Qwen3-VL-8B-Instruct-GGUF/resolve/main/Qwen3-VL-8B-Instruct-Q4_K_M.gguf" (Join-Path $mdir "Qwen3-VL-8B-Instruct-Q4_K_M.gguf")
    Fetch-Gguf "https://huggingface.co/unsloth/Qwen3-VL-8B-Instruct-GGUF/resolve/main/mmproj-F16.gguf"                 (Join-Path $mdir "Qwen3-VL-8B-mmproj-F16.gguf")
    Info "Vision ready: confirm by loading one image (Describe in the Library); if it errors with a clip/tensor mismatch, bump llama.cpp."
}

# ---- LLM bake-off candidates (optional, large): the coder/heavy challengers to eval vs the incumbents ----
# After download, uncomment the matching block in serving\llama-swap.yaml, then gate via serving\test-toolcall.ps1
# + evals\run-suite.ps1 (>=91% golden AND zero tool-call flakes) BEFORE making it a tier model. Run text-only.
if ($LlmCandidates) {
    Info "LLM bake-off candidates (Qwen3.6-27B / Qwen3.6-35B-A3B / Qwen3-Coder-Next-80B — ~60GB total)"
    $mdir = Join-Path $root "models"
    Fetch-Gguf "https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/Qwen3.6-27B-UD-Q4_K_XL.gguf"                       (Join-Path $mdir "Qwen3.6-27B-UD-Q4_K_XL.gguf")
    Fetch-Gguf "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"               (Join-Path $mdir "Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf")
    Fetch-Gguf "https://huggingface.co/unsloth/Qwen3-Coder-Next-GGUF/resolve/main/Qwen3-Coder-Next-80B-A3B-Q4_K_XL.gguf"        (Join-Path $mdir "Qwen3-Coder-Next-80B-A3B-Q4_K_XL.gguf")
    Info "Candidates downloaded. Uncomment the matching llama-swap.yaml block, then run the eval gate before adopting."
}

# ---- TTS stack: uncensored speech + zero-shot voice cloning (Chatterbox) — optional, works with or without -Media ----
if ($Tts) {
    Info "TTS stack (Chatterbox: uncensored speech + zero-shot voice cloning)"
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Ensure-WinGet "Python.Python.3.10" "python" }
    Ensure-WinGet "Git.Git" "git"
    $ttsRoot = Join-Path $root "tts\Chatterbox-TTS-Server"
    if (-not (Test-Path (Join-Path $ttsRoot ".git"))) { Info "cloning Chatterbox-TTS-Server ..."; Git-Clone https://github.com/devnen/Chatterbox-TTS-Server $ttsRoot } else { Ok "Chatterbox-TTS-Server cloned" }
    $tpy = Join-Path $ttsRoot ".venv\Scripts\python.exe"
    $tok = Join-Path $ttsRoot ".venv\.deps-ok"   # sentinel: written only after ALL deps succeed
    if (-not (Test-Path $tok)) {
        Info "creating venv + installing cu128 torch + deps (large, ~3GB) ..."
        if (-not (Test-Path $tpy)) { python -m venv (Join-Path $ttsRoot ".venv") }   # reuse a partial venv; pip resumes
        & $tpy -m pip install --upgrade pip | Out-Null
        Pip $tpy install -r (Join-Path $ttsRoot "requirements-nvidia-cu128.txt")
        # chatterbox itself with --no-deps so it can't downgrade the cu128 torch
        Pip $tpy install --no-deps "git+https://github.com/devnen/chatterbox-v2.git@master" s3tokenizer==0.3.0 onnx==1.16.0
        # onnx needs protobuf >=3.20 but the cu128 reqs pin 3.19.6 (descript-audiotools) — fix it
        Pip $tpy install protobuf==4.25.5
        New-Item -ItemType File -Force $tok | Out-Null   # all deps verified — safe to skip next run
    } else { Ok "TTS venv present" }
    # Use the public ORIGINAL model — the server's default 'chatterbox-turbo' repo is gated. ALSO pin the
    # bind to loopback: Chatterbox ships host: 0.0.0.0 (auth off), which would expose the voice-clone +
    # speech API to the whole LAN — every sibling DokiDex server is 127.0.0.1, so match them.
    $cfg = Join-Path $ttsRoot "config.yaml"
    if (Test-Path $cfg) { (Get-Content $cfg) -replace 'repo_id: chatterbox-turbo', 'repo_id: chatterbox' -replace '(?m)^(\s*)host:\s*0\.0\.0\.0', '${1}host: 127.0.0.1' | Set-Content $cfg }
    # Strip the Perth watermark in every chatterbox model file (genuinely unmarked, uncensored output).
    $cbDir = Join-Path $ttsRoot ".venv\Lib\site-packages\chatterbox"
    foreach ($f in "tts.py", "mtl_tts.py", "tts_turbo.py", "vc.py") {
        $fp = Join-Path $cbDir $f
        if (Test-Path $fp) { (Get-Content $fp) -replace 'self\.watermarker\.apply_watermark\(wav, sample_rate=self\.sr\)', 'wav  # watermark stripped (DokiDex: uncensored)' | Set-Content $fp }
    }
    Ok "TTS ready -> :8004 (OpenAI /v1/audio/speech + voice cloning). First '.\doki.ps1 up' downloads the voice model."
}

# ---- Kokoro: GATED fast/light TTS alternative (Kokoro-82M via remsky/Kokoro-FastAPI) — additive, NOT a replacement ----
# The census DEFER-on-the-default outcome (docs/decisions.md) keeps Chatterbox :8004 the byte-for-byte default and
# wires the ONE worthwhile gated alternative: Kokoro-82M (hexgrad, Apache-2.0 — https://huggingface.co/hexgrad/Kokoro-82M)
# behind remsky/Kokoro-FastAPI (https://github.com/remsky/Kokoro-FastAPI), a mature OpenAI-compatible
# /v1/audio/speech server. <2GB VRAM, CPU-capable, RTF ~0.03 -> snappy narration with near-zero GPU contention, so
# it ALSO coexists in agent mode. It has NO voice cloning (54 fixed preset voices), so it can NEVER be the
# custom-voice default — strictly a toggle. ADDITIVE: own venv, loopback-bound, NEW port :8006 (not :8004
# Chatterbox / :8005 STT). This block NEVER touches the devnen Chatterbox clone or its :8004 bind.
if ($Kokoro) {
    Info "Kokoro stack (fast/light Apache-2.0 TTS via remsky/Kokoro-FastAPI — gated alternative; NO cloning)"
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Ensure-WinGet "Python.Python.3.10" "python" }
    Ensure-WinGet "Git.Git" "git"
    # Kokoro phonemizes via espeak-ng; the launcher points PHONEMIZER_ESPEAK_LIBRARY at libespeak-ng.dll, so the
    # DLL must be on disk or synthesis fails. Install it here (same Ensure-WinGet discipline as python/git). The MSI
    # drops it at C:\Program Files\eSpeak NG\libespeak-ng.dll (an absolute path the launcher reads), so verify by
    # DLL-on-disk rather than a PATH command (the wix PATH entry may not refresh mid-session -> a false throw).
    Ensure-WinGet "eSpeak-NG.eSpeak-NG" $null
    if (-not (Test-Path "C:\Program Files\eSpeak NG\libespeak-ng.dll")) {
        Warn "espeak-ng DLL not found at C:\Program Files\eSpeak NG\libespeak-ng.dll — Kokoro phonemization will fail until eSpeak-NG is installed."
    }
    $kRoot = Join-Path $root "kokoro\Kokoro-FastAPI"
    if (-not (Test-Path (Join-Path $kRoot ".git"))) { Info "cloning Kokoro-FastAPI ..."; Git-Clone https://github.com/remsky/Kokoro-FastAPI $kRoot } else { Ok "Kokoro-FastAPI cloned" }
    $kpy = Join-Path $kRoot ".venv\Scripts\python.exe"
    $kok = Join-Path $kRoot ".venv\.deps-ok"   # sentinel: written only after ALL deps succeed (resumable, like -Tts)
    if (-not (Test-Path $kok)) {
        Info "creating venv + installing Kokoro-FastAPI + cu128 torch + deps ..."
        if (-not (Test-Path $kpy)) { python -m venv (Join-Path $kRoot ".venv") }   # reuse a partial venv; pip resumes
        & $kpy -m pip install --upgrade pip | Out-Null
        # cu128 torch first so the project deps can't pull a CPU/older wheel and downgrade it (same discipline as -Tts).
        Pip $kpy install torch --index-url https://download.pytorch.org/whl/cu128
        # Install the package + its GPU deps via the repo's cu128 extra. Notes (plain pip, NOT uv):
        #   - Use [gpu-cu128] NOT [gpu]: the [gpu] extra pins torch==2.8.0+cu126 (a DIFFERENT local version than the
        #     cu128 wheel installed above, so plain pip would try to re-resolve it and fail). [gpu-cu128] pins
        #     2.8.0+cu128, which the already-installed wheel satisfies.
        #   - The editable-extras spec is "<dir>[extra]" with the suffix on the dir (NO "\." Join-Path separator,
        #     which would yield a bogus ...\Kokoro-FastAPI\.[gpu-cu128] path segment).
        #   - The repo declares its torch index in [tool.uv.sources], which plain pip IGNORES — so pass the cu128
        #     index explicitly so pip can resolve the +cu128 local-version pin.
        Pip $kpy install -e "$kRoot[gpu-cu128]" --extra-index-url https://download.pytorch.org/whl/cu128
        # Fetch the Kokoro-82M weights NOW (install-time), not on first request. Kokoro-FastAPI ships an explicit
        # model-download step — `docker/scripts/download_model.py --output api/src/models/v1_0` (confirmed against the
        # upstream repo README + the script's argparse: it pulls kokoro-v1_0.pth + config.json from the v0.1.4 release
        # into the --output dir). The server's loader resolves the weight via settings.model_dir +
        # pytorch_kokoro_v1_file = "v1_0/kokoro-v1_0.pth" (api/src/core/{config,model_config}.py), so the launcher's
        # MODEL_DIR=src\models (-> <repo>\api\src\models, the loader appends v1_0\) reads EXACTLY this --output dir.
        # IDEMPOTENT: skip if the .pth already exists (resumable re-runs). WARN-on-failure: a download hiccup must not
        # abort the whole -Kokoro block (the venv is already provisioned; the weight is retryable on the next run).
        $kModelDir = Join-Path $kRoot "api\src\models\v1_0"
        $kWeight   = Join-Path $kModelDir "kokoro-v1_0.pth"
        if (-not (Test-Path $kWeight)) {
            Info "downloading Kokoro-82M weights -> api\src\models\v1_0 ..."
            try { & $kpy (Join-Path $kRoot "docker\scripts\download_model.py") --output $kModelDir; if ($LASTEXITCODE -ne 0) { throw "download_model.py exited $LASTEXITCODE" } }
            catch { Warn "Kokoro weight download failed ($($_.Exception.Message)) — re-run setup.ps1 -Kokoro, or manually:  & `"$kpy`" `"$kRoot\docker\scripts\download_model.py`" --output `"$kModelDir`"" }
        } else { Ok "Kokoro-82M weights present" }
        New-Item -ItemType File -Force $kok | Out-Null   # all deps verified — safe to skip next run
    } else { Ok "Kokoro venv present" }
    # Loopback-bind on the NEW :8006 port so it's additive AND never LAN-exposed (every sibling DokiDex server is
    # 127.0.0.1). The bind is set by uvicorn's `--host 127.0.0.1 --port 8006` CLI flags in start-kokoro.ps1 (uvicorn
    # binds from its own argv, NOT from Kokoro-FastAPI's .env) — the .env HOST/PORT below is only a fallback for a
    # bare manual `python -m uvicorn` run that doesn't pass the flags, so pin it to loopback/:8006 too.
    $kEnv = Join-Path $kRoot ".env"
    if (-not (Test-Path $kEnv)) { Set-Content $kEnv "HOST=127.0.0.1`nPORT=8006" }
    # ON-GPU LABELED: the URLs/extras below are upstream-sourced (pyproject version = "0.6.0-rc1", the [gpu-cu128]
    # extra pinning torch==2.8.0+cu128, the cu128 index, docker/scripts/download_model.py -> api/src/models/v1_0 — all
    # confirmed against the remsky/Kokoro-FastAPI repo), but the live stack is a first-run-on-GPU confirm: (1) the
    # cu128 torch + .[gpu-cu128] resolve cleanly under plain pip, (2) download_model.py lands kokoro-v1_0.pth where
    # the launcher's MODEL_DIR reads, (3) espeak-ng phonemization loads via PHONEMIZER_ESPEAK_LIBRARY, and (4)
    # /v1/audio/speech actually synthesizes on the GPU.
    Ok "Kokoro ready -> :8006 (OpenAI /v1/audio/speech, fixed preset voices, NO cloning). Weights are downloaded NOW by setup.ps1 -Kokoro (install-time); first '.\doki.ps1 up' just starts the already-provisioned server."
}

# ---- OCR: GATED scanned/image-PDF text extraction for the KB ("chat with your documents") — additive, opt-in ----
# Closes the explicit v0.15 gap: today a scanned/photographed PDF extracts to ~empty text via pypdf -> 0 chunks +
# the benign "looks scanned (OCR not supported)" hint. With -Ocr, doc_index._extract_pdf renders the pages
# (pymupdf, bundled MuPDF — NO poppler/ghostscript) + OCRs them (pytesseract -> the Tesseract binary) and feeds
# the text into the EXISTING chunk->embed->store pipeline UNCHANGED. A normal TEXT PDF never touches any of this
# (no OCR, no new import). CPU-only, no always-on server, no GPU.
if ($Ocr) {
    Info "OCR for scanned/image PDFs (Tesseract UB-Mannheim + pymupdf/pytesseract/Pillow) — gated KB add-on"
    # Tesseract: the UB-Mannheim Windows build (5.x, bundles English tessdata) — THE community-standard build every
    # Python/Windows tutorial targets. Its installer (an NSIS/Nullsoft setup.exe, NOT an MSI) does NOT add
    # tesseract.exe to PATH by default (the PATH checkbox was removed to avoid truncating a long PATH), so verify by
    # the fixed install path the installer drops — exactly as -Kokoro verifies the espeak-ng DLL on disk rather than
    # a PATH command. doc_index.py points pytesseract.tesseract_cmd at this same path (TESSERACT_CMD overrides) so
    # OCR works without a PATH edit.
    Ensure-WinGet "UB-Mannheim.TesseractOCR" $null
    $tess = "C:\Program Files\Tesseract-OCR\tesseract.exe"
    if (Test-Path $tess) { Ok "Tesseract present -> $tess" }
    else { Warn "Tesseract not found at $tess — scanned-PDF OCR will no-op until UB-Mannheim.TesseractOCR is installed (re-run setup.ps1 -Ocr)." }
    # The pip parsers are pure-pip wheels resolved on-demand by uv on the doc_ingest_bin `--with` overlay (see
    # DocSearch.cs ParserWith) — NOT a venv here. Warm uv's cache now so the first scanned-PDF ingest isn't slow:
    if (Get-Command uv -ErrorAction SilentlyContinue) {
        Info "warming the uv OCR-parser cache (pymupdf/pytesseract/Pillow) ..."
        try { uv pip install --system pymupdf pytesseract Pillow 2>$null | Out-Null } catch {}
        # (non-fatal: the live ingest path resolves them via `uv run --with ...` regardless; this just pre-caches.)
    }
    Ok "OCR add-on installed (scanned PDFs now ingest into the KB; a text PDF is unchanged)."
}

# ---- Demucs: standalone audio stem separation (vocals/drums/bass/other) — optional, model-free DSP ----
if ($Demucs) {
    Info "Demucs (audio stem separation: htdemucs / htdemucs_6s)"
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Ensure-WinGet "Python.Python.3.10" "python" }
    $dRoot = Join-Path $root "audio-tools\demucs"
    $dpy = Join-Path $dRoot ".venv\Scripts\python.exe"
    $dok = Join-Path $dRoot ".venv\.deps-ok"
    if (-not (Test-Path $dok)) {
        Info "creating venv + installing demucs (+ cu128 torch) ..."
        New-Item -ItemType Directory -Force $dRoot | Out-Null
        if (-not (Test-Path $dpy)) { python -m venv (Join-Path $dRoot ".venv") }
        & $dpy -m pip install --upgrade pip | Out-Null
        Pip $dpy install torch --index-url https://download.pytorch.org/whl/cu128
        Pip $dpy install demucs
        New-Item -ItemType File -Force $dok | Out-Null
    } else { Ok "Demucs venv present" }
    Ok "Demucs ready -> audio-tools/demucs (the studio 'stems' action on any audio card runs it)."
}

# ---- SAM: Segment-Anything point segmentation (semantic click->mask in the edit canvas) — optional ----
if ($Sam) {
    Info "SAM (Segment-Anything: semantic click-to-mask)"
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Ensure-WinGet "Python.Python.3.10" "python" }
    $samRoot = Join-Path $root "audio-tools\sam"
    $spy = Join-Path $samRoot ".venv\Scripts\python.exe"
    $sok = Join-Path $samRoot ".venv\.deps-ok"
    if (-not (Test-Path $sok)) {
        Info "creating venv + installing segment-anything (+ cu128 torch) ..."
        New-Item -ItemType Directory -Force $samRoot | Out-Null
        if (-not (Test-Path $spy)) { python -m venv (Join-Path $samRoot ".venv") }
        & $spy -m pip install --upgrade pip | Out-Null
        Pip $spy install torch --index-url https://download.pytorch.org/whl/cu128
        Pip $spy install "git+https://github.com/facebookresearch/segment-anything.git" pillow numpy
        New-Item -ItemType File -Force $sok | Out-Null
    } else { Ok "SAM venv present" }
    $ckpt = Join-Path $samRoot "sam_vit_b.pth"
    if (-not (Test-Path $ckpt)) {
        Info "downloading SAM vit_b checkpoint (~375MB) ..."
        Invoke-WebRequest "https://dl.fbaipublicfiles.com/segment_anything/sam_vit_b_01ec64.pth" -OutFile $ckpt
    } else { Ok "SAM checkpoint present" }
    Ok "SAM ready -> audio-tools/sam (the edit canvas 'SAM' click mode uses it; magic-wand works without it)."
}

# ---- LoRA training: kohya sd-scripts — optional, GPU training ----
if ($Train) {
    Info "LoRA trainer (kohya sd-scripts)"
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Ensure-WinGet "Python.Python.3.10" "python" }
    Ensure-WinGet "Git.Git" "git"
    $tRoot = Join-Path $root "audio-tools\sd-scripts"
    if (-not (Test-Path (Join-Path $tRoot ".git"))) { Info "cloning kohya sd-scripts ..."; Git-Clone https://github.com/kohya-ss/sd-scripts $tRoot } else { Ok "sd-scripts cloned" }
    $trpy = Join-Path $tRoot ".venv\Scripts\python.exe"
    $trok = Join-Path $tRoot ".venv\.deps-ok"
    if (-not (Test-Path $trok)) {
        Info "creating venv + installing trainer deps (+ cu128 torch, large) ..."
        if (-not (Test-Path $trpy)) { python -m venv (Join-Path $tRoot ".venv") }
        & $trpy -m pip install --upgrade pip | Out-Null
        Pip $trpy install torch --index-url https://download.pytorch.org/whl/cu128
        Pip $trpy install -r (Join-Path $tRoot "requirements.txt")
        Pip $trpy install accelerate bitsandbytes
        New-Item -ItemType File -Force $trok | Out-Null
    } else { Ok "trainer venv present" }
    Ok "LoRA trainer ready -> audio-tools/sd-scripts (the studio 'Train' action builds a LoRA into Models/Lora; needs a base model sd-scripts supports)."
}

# ---- STT stack: fully-local speech-to-text (NVIDIA Parakeet via onnx-asr) — optional ----
if ($Stt) {
    Info "STT stack (Parakeet TDT 0.6B v2 via onnx-asr: local speech-to-text)"
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Ensure-WinGet "Python.Python.3.10" "python" }
    $sttRoot = Join-Path $root "stt"
    $spy = Join-Path $sttRoot ".venv\Scripts\python.exe"
    $sok = Join-Path $sttRoot ".venv\.deps-ok"   # sentinel: written only after ALL deps succeed
    if (-not (Test-Path $sok)) {
        Info "creating venv + installing onnx-asr (CPU EP) + FastAPI (~300MB) ..."
        New-Item -ItemType Directory -Force $sttRoot | Out-Null
        if (-not (Test-Path $spy)) { python -m venv (Join-Path $sttRoot ".venv") }   # reuse a partial venv; pip resumes
        & $spy -m pip install --upgrade pip | Out-Null
        # onnx-asr[cpu,hub] pulls onnxruntime + huggingface_hub; soundfile loads/resamples audio
        Pip $spy install "onnx-asr[cpu,hub]" fastapi "uvicorn[standard]" python-multipart soundfile
        New-Item -ItemType File -Force $sok | Out-Null   # all deps verified — safe to skip next run
    } else { Ok "STT venv present" }
    Ok "STT ready -> :8005 (OpenAI /v1/audio/transcriptions). First '.\doki.ps1 up' downloads the Parakeet model (~2GB)."
}

# ---- 4b. Control panel: .NET 9 SDK + build the app + create the launcher ---------------------------
# Dev repo: the panel (control/, net9.0-windows WPF) is the PRIMARY UI, so provision the .NET 9 SDK and
# build it (else control.bat / `doki panel` hits a hard build failure). The .NET 9 SDK is the single SDK
# for the whole stack — it bundles the WindowsDesktop runtime AND builds SwarmUI's net8.0 too.
# Managed (all-in-one) install: the panel IS this self-contained exe — already built, ships its own
# runtime, and control/ isn't even in the payload. So skip the SDK + build entirely; the media stack
# below fetches the SDK on its own only if SwarmUI needs it. This is why a core-only managed install
# pulls NO .NET SDK: the app is baked into the exe, not rebuilt from source.
if ($Managed) {
    Ok "control panel = this app (managed install) — no SDK/build needed"
} else {
    Ensure-DotNet9
    $panelProj = Join-Path $root "control\DokiDex.Control.csproj"
    if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $panelProj)) {
        Info "building control panel ..."
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
        dotnet build $panelProj -c Release | Out-Null
        if ($LASTEXITCODE -eq 0) {
            try { & pwsh -NoProfile -File (Join-Path $root "control\make-shortcut.ps1") | Out-Null } catch {}
            Ok "control panel built — double-click DokiDex.lnk (or run: .\doki.ps1 panel)"
        } else { Warn "panel build failed — run .\control.bat to see the build errors" }
    } else { Warn "dotnet not found — run .\control.bat to build + launch the panel" }
}

if (-not $Media) { Info "core setup done — launch via DokiDex.lnk (or '.\doki.ps1 panel').  -Media adds image/video,  -Tts speech,  -Stt transcription."; return }

# ---- 5. Media stack: SwarmUI + ComfyUI + uncensored models ----------------
Info "media stack (SwarmUI + ComfyUI + uncensored image/video models)"

# 5a. prereqs: git + the .NET 9 SDK (it builds SwarmUI's net8.0). A managed install skipped the SDK in
#     4b (the panel ships prebuilt), so ensure it here — SwarmUI still needs it. Idempotent either way.
Ensure-WinGet "Git.Git" "git"
Ensure-DotNet9

# 5b. clone SwarmUI
$swarm = Join-Path $root "media\SwarmUI"
if (-not (Test-Path (Join-Path $swarm ".git"))) { Info "cloning SwarmUI ..."; Git-Clone https://github.com/mcmonkeyprojects/SwarmUI $swarm } else { Ok "SwarmUI cloned" }

# 5b1. Optionally PIN SwarmUI to a known-good commit. Its HTTP/WS API is untyped + unversioned, so a bare
# `git pull` can change the gen/model contract the panel + web studio drive (GenerateText2ImageWS frame
# keys, DoModelDownloadWS, GenerateText2Image body). Set $env:DOKI_SWARM_COMMIT to your verified commit to
# freeze it (recommended once you've confirmed a working build); empty = track upstream main (default).
$swarmPin = $env:DOKI_SWARM_COMMIT
if ($swarmPin) {
    Info "pinning SwarmUI to $swarmPin ..."
    git -C $swarm fetch --quiet origin 2>$null
    git -C $swarm checkout --quiet $swarmPin
    if ($LASTEXITCODE -ne 0) { Warn "could not checkout SwarmUI $swarmPin (staying on current HEAD)" } else { Ok "SwarmUI pinned to $swarmPin" }
}

# 5b2. install the MagicPrompt extension (local-LLM prompt enhancement) BEFORE the build so it
#      compiles in. Adding it forces a rebuild even if SwarmUI was already built.
$mpExt = Join-Path $swarm "src\Extensions\SwarmUI-MagicPromptExtension"
$extAdded = $false
if (-not (Test-Path $mpExt)) { Info "installing MagicPrompt extension ..."; Git-Clone https://github.com/HartsyAI/SwarmUI-MagicPromptExtension $mpExt; $extAdded = $true } else { Ok "MagicPrompt extension present" }

# 5b3. install the DokiGen Void theme — the on-brand void/cyan/gold SwarmUI skin (matches the control
#      panel) — from the committed asset, BEFORE the build so it compiles in. Copy (not clone) so repo
#      updates propagate; force a rebuild on first install so an already-built SwarmUI picks it up.
$dgExt = Join-Path $swarm "src\Extensions\SwarmUI-DokiGenTheme"
$dgSrc = Join-Path $root "media-assets\SwarmUI-DokiGenTheme"
if (Test-Path $dgSrc) {
    $dgNew = -not (Test-Path $dgExt)
    if (Test-Path $dgExt) { Remove-Item $dgExt -Recurse -Force }
    Copy-Item $dgSrc $dgExt -Recurse -Force
    if ($dgNew) { Info "installing DokiGen Void theme ..."; $extAdded = $true } else { Ok "DokiGen Void theme present (refreshed)" }
} else { Warn "media-assets\SwarmUI-DokiGenTheme not present; skipping DokiGen theme" }

# 5c. build SwarmUI (rebuild when missing, a new extension was added, OR the checkout advanced past
#     the last build — the last_build sentinel was written but never read, so a `git pull` of SwarmUI
#     previously kept running a stale binary).
$swarmExe = Join-Path $swarm "src\bin\live_release\SwarmUI.exe"
$builtAt = Get-Content (Join-Path $swarm "src\bin\last_build") -ErrorAction SilentlyContinue
$headAt  = git -C $swarm rev-parse HEAD 2>$null
if ((-not (Test-Path $swarmExe)) -or $extAdded -or ($headAt -and $headAt -ne $builtAt)) {
    Info "building SwarmUI ..."
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    dotnet build (Join-Path $swarm "src\SwarmUI.csproj") --configuration Release -o (Join-Path $swarm "src\bin\live_release")
    if ($LASTEXITCODE -ne 0) { throw "SwarmUI build failed" }
    git -C $swarm rev-parse HEAD | Set-Content (Join-Path $swarm "src\bin\last_build")
} else { Ok "SwarmUI built" }

# 5d. launch headless if not already serving
function Swarm-Up { try { Invoke-WebRequest "http://127.0.0.1:7801/" -TimeoutSec 3 -UseBasicParsing | Out-Null; $true } catch { $false } }
if (-not (Swarm-Up)) {
    Info "launching SwarmUI ..."
    Start-Process -FilePath $swarmExe -ArgumentList "--launch_mode","none","--host","127.0.0.1","--port","7801" -WorkingDirectory $swarm -WindowStyle Hidden | Out-Null
    for ($i = 0; $i -lt 60 -and -not (Swarm-Up); $i++) { Start-Sleep 1 }
}
if (Swarm-Up) { Ok "SwarmUI serving :7801" } else { throw "SwarmUI failed to start" }

# 5d2. make DokiGen Void the default theme. Only EDIT an existing Settings.fds (never create a partial
#      one — that could break SwarmUI startup); takes effect on the next SwarmUI start. The per-browser
#      sui_theme_id cookie still wins once a user picks a theme in the UI.
$fds = Join-Path $swarm "Data\Settings.fds"
if (Test-Path $fds) {
    $s = [System.IO.File]::ReadAllText($fds)
    # The Theme key lives INDENTED under the DefaultUser: block (e.g. "`tTheme: modern_dark"), so the
    # anchor must allow AND preserve leading whitespace. The old column-0 '^Theme:' never matched, so
    # the default was silently left at modern_dark on every install — this is why the theme looked unchanged.
    if ($s -match '(?m)^(\s*)Theme:\s*\S+') {
        $s2 = [regex]::Replace($s, '(?m)^(\s*)Theme:\s*\S+', '${1}Theme: dokigen')
        if ($s2 -ne $s) { [System.IO.File]::WriteAllText($fds, $s2); Ok "DokiGen Void set as default SwarmUI theme (applies next start)" } else { Ok "DokiGen Void already the default theme" }
    } else { Ok "DokiGen Void installed — select it in User Settings -> Theme" }
} else { Ok "DokiGen Void installed — select it in User Settings -> Theme" }

# 5e. headless ComfyUI backend install (the verified InstallConfirmWS flow)
if (-not (Test-Path (Join-Path $swarm "dlbackend\comfy"))) {
    Info "installing ComfyUI backend headlessly (~2GB download) ..."
    $installed = $false
    # A CancellationTokenSource (not CancellationToken.None) so a 20-min stall actually INTERRUPTS a
    # blocked ReceiveAsync — the old wall-clock $deadline was only the while-condition and could never
    # break out of a receive that hangs (dead TCP / stuck pip / AV folder lock during the multi-minute
    # zero-frame install windows), hanging the whole one-command bootstrap forever.
    $cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(20)); $ct = $cts.Token
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    try {
        $sid = (Invoke-RestMethod "http://127.0.0.1:7801/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $ws.ConnectAsync([Uri]"ws://127.0.0.1:7801/API/InstallConfirmWS", $ct).GetAwaiter().GetResult() | Out-Null
        $payload = @{ session_id = $sid; theme = "modern_dark"; installed_for = "just_self"; backend = "comfyui"; models = "none"; install_amd = $false; language = "en"; make_shortcut = $false } | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        $ws.SendAsync([System.ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).GetAwaiter().GetResult() | Out-Null
        $buf = New-Object byte[] 32768; $sb = New-Object System.Text.StringBuilder
        while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $r = $ws.ReceiveAsync([System.ArraySegment[byte]]::new($buf), $ct).GetAwaiter().GetResult()
            if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { break }
            [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
            if ($r.EndOfMessage) {
                $f = $sb.ToString(); [void]$sb.Clear()
                if ($f -match '"info":"([^"]*)"') { Info $Matches[1] }
                if ($f -match '"success"\s*:\s*true') { $installed = $true; break }
                if ($f -match '"error"') { throw "backend install error: $f" }
            }
        }
    }
    catch { throw "ComfyUI backend install failed/timed out: $($_.Exception.Message)" }
    finally { $ws.Dispose(); $cts.Dispose() }
    # SwarmUI creates dlbackend\comfy EARLY (right after the 7z extract, before VC-redist + the
    # multi-minute pip install), so its existence does NOT mean success. Require the explicit success
    # frame; otherwise nuke the partial dir so the existence gate above reinstalls cleanly next run.
    if (-not $installed) {
        Remove-Item (Join-Path $swarm "dlbackend\comfy") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $swarm "dlbackend\tmpcomfy") -Recurse -Force -ErrorAction SilentlyContinue
        throw "ComfyUI backend install did not report success (partial install removed; re-run setup.ps1 -Media)"
    }
    Ok "ComfyUI backend installed"
} else { Ok "ComfyUI backend present" }

# 5f. download uncensored models (idempotent)
$diff = Join-Path $swarm "Models\diffusion_models"; New-Item -ItemType Directory -Force $diff | Out-Null
$loraD = Join-Path $swarm "Models\Lora"; New-Item -ItemType Directory -Force $loraD | Out-Null
function Get-Model($url, $dest) {
    if (Test-Path $dest) { Ok "have $(Split-Path $dest -Leaf)"; return }
    if (-not $url) { Warn "could not resolve $(Split-Path $dest -Leaf)"; return }
    Info "downloading $(Split-Path $dest -Leaf) ..."
    # Download to a .part temp and atomically promote on success, so an interrupted or failed
    # download never leaves a truncated file that the existence-only gate above treats as done.
    # -C - resumes a .part left by a hard kill; on soft failure we delete the .part and retry
    # fresh next run (this also self-heals the rare full-but-unpromoted .part that would 416).
    # NB: -C - and --remove-on-error are MUTUALLY EXCLUSIVE in curl (exit 2) — our own cleanup
    # replaces --remove-on-error; do not re-add it.
    $tmp = "$dest.part"
    curl.exe -L --fail --retry 3 -C - -o $tmp $url
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        Warn "download failed: $url"; return
    }
    Move-Item -Force $tmp $dest
    Ok "$(Split-Path $dest -Leaf) ($([math]::Round((Get-Item $dest).Length/1GB,2)) GB)"
}
# --- lean: the verified reliable defaults ---
# image: Z-Image Turbo (uncensored, fast, photoreal) — verified ~seconds/image
Get-Model "https://huggingface.co/mcmonkey/swarm-models/resolve/main/SwarmUI_Z-Image-Turbo-FP8Mix.safetensors" (Join-Path $diff "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors")
# video: Wan 2.1 1.3B (uncensored) — fits 32GB with headroom, ~25s/clip. The reliable default.
Get-Model "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files/diffusion_models/wan2.1_t2v_1.3B_fp16.safetensors" (Join-Path $diff "wan2.1_t2v_1.3B_fp16.safetensors")

# --- full: the "Sora-2-from-simple-prompts" quality kit (~90-100GB). The lean floor
#     above (Z-Image Turbo + Wan 2.1 1.3B) is untouched and always present as the
#     reliable fallback. All URLs are hardcoded + HF-tree-verified (no regex resolver). ---
if ($Models -eq "full") {
    $te    = Join-Path $swarm "Models\text_encoders"; New-Item -ItemType Directory -Force $te    | Out-Null
    $vae   = Join-Path $swarm "Models\vae";           New-Item -ItemType Directory -Force $vae   | Out-Null
    # Foley models go in ComfyUI's OWN models dir (where the phazei node's folder_paths looks) —
    # NOT SwarmUI's Models\, which doesn't map this custom subfolder to the ComfyUI backend.
    $foley = Join-Path $swarm "dlbackend\comfy\ComfyUI\models\foley"; New-Item -ItemType Directory -Force $foley | Out-Null
    $modelsDir = Join-Path $root "models"

    # Video tier: Wan 2.2 14B MoE (high+low noise pair, fp8_scaled). NOTE: the fp8 dual-expert does
    # NOT fit 32GB in SwarmUI's StepSwap (state is held across the high->low handoff) — it OOM'd >300s
    # live (docs\decisions.md 2026-06-14). Kept on disk only for the eval-gated GGUF-Q4 A/B; the default
    # video path is the Wan 2.2 5B (serving\doki-gen.ps1). Downloaded with -Models full.
    # NOTE: Wan 2.5/2.6/2.7 are API-only — Wan 2.2 is the newest OPEN-weight Wan that exists.
    $w22 = "https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged/resolve/main/split_files"
    Get-Model "$w22/diffusion_models/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors" (Join-Path $diff "wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors"  (Join-Path $diff "wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors" (Join-Path $diff "wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors"  (Join-Path $diff "wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_ti2v_5B_fp16.safetensors"                  (Join-Path $diff "wan2.2_ti2v_5B_fp16.safetensors")    # fast preview; no fp8 exists

    # Quality-video tier: Wan 2.2 T2V A14B GGUF dual-expert (Q4_K_M, ~9.65GB each — the size cut vs the fp8
    # ~13.3GB experts above that OOM'd). This is the ONLY zero-OOM 14B route (docs\decisions.md 2026-06-14/16):
    # at Q4_K_M the high+low pair (~19.3GB) fits 32GB in SwarmUI's StepSwap. serving\doki-gen.ps1's video arm
    # uses these on `doki gen -Video -Quality` (base = HIGH-noise, Refiner Model = LOW-noise, StepSwap @ 0.5).
    # QuantStack tree is NOT flat — the resolve URL includes the HighNoise/ or LowNoise/ subfolder. Same TE
    # (umt5_xxl) + VAE (wan2.2_vae) as the 5B/14B below — NOT re-downloaded. GATED on-GPU: the city96
    # ComfyUI-GGUF node + live 32GB fit + the `refinermodel` body key are the labeled remaining confirms.
    $t2vgguf = "https://huggingface.co/QuantStack/Wan2.2-T2V-A14B-GGUF/resolve/main"
    Get-Model "$t2vgguf/HighNoise/Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf" (Join-Path $diff "Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf")
    Get-Model "$t2vgguf/LowNoise/Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf"   (Join-Path $diff "Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf")

    Get-Model "$w22/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors"              (Join-Path $te   "umt5_xxl_fp8_e4m3fn_scaled.safetensors")
    Get-Model "$w22/vae/wan2.2_vae.safetensors"                                        (Join-Path $vae  "wan2.2_vae.safetensors")             # used by the Wan 2.2 5B AND 14B models (WanFoley node 101 loads this)
    Get-Model "$w22/vae/wan_2.1_vae.safetensors"                                       (Join-Path $vae  "wan_2.1_vae.safetensors")            # for the Wan 2.1 1.3B floor (NOT the 5B — corrected)

    # Wan2.2-Lightning 4-step distill LoRAs (HIGH+LOW per model) = the "fast" preset.
    # Source filenames are generic (high_noise_model.safetensors); RENAME on save so they don't collide.
    $lite = "https://huggingface.co/lightx2v/Wan2.2-Lightning/resolve/main"
    Get-Model "$lite/Wan2.2-T2V-A14B-4steps-lora-rank64-Seko-V2.0/high_noise_model.safetensors" (Join-Path $loraD "Wan22-Lightning-T2V-HIGH.safetensors")
    Get-Model "$lite/Wan2.2-T2V-A14B-4steps-lora-rank64-Seko-V2.0/low_noise_model.safetensors"  (Join-Path $loraD "Wan22-Lightning-T2V-LOW.safetensors")
    Get-Model "$lite/Wan2.2-I2V-A14B-4steps-lora-rank64-Seko-V1/high_noise_model.safetensors"   (Join-Path $loraD "Wan22-Lightning-I2V-HIGH.safetensors")
    Get-Model "$lite/Wan2.2-I2V-A14B-4steps-lora-rank64-Seko-V1/low_noise_model.safetensors"    (Join-Path $loraD "Wan22-Lightning-I2V-LOW.safetensors")

    # Image quality ceiling: Z-Image Base (non-distilled) — now the DEFAULT image model for `doki gen`,
    # reusing the qwen_3_4b text encoder + Flux ae VAE SwarmUI auto-fetched for Turbo (now the -Fast tier).
    Get-Model "https://huggingface.co/Comfy-Org/z_image/resolve/main/split_files/diffusion_models/z_image_bf16.safetensors" (Join-Path $diff "z_image_bf16.safetensors")

    # FLUX.2 Klein 4B — BFL's distilled small FLUX.2. SwarmUI's own docs note the 4B "often seems smarter"
    # than the 9B, and the 4B + Qwen-4B TE footprint (~7.75GB unet + 8.04GB TE + 0.34GB VAE) sits comfortably
    # in 32GB at bf16. Selected via  doki gen -Model flux-2-klein-4b.safetensors  — the Build-GenBody family
    # override then applies the FLUX.2 sampler knobs (euler + the Flux2 specialty scheduler), no recipe edit.
    #   IMPORTANT — the official black-forest-labs/FLUX.2-klein repo is GATED (HF 401 unauthenticated); use the
    #   NON-GATED Comfy-Org/flux2-klein-4B repackage (same pattern as z_image / Wan / ACE-Step / Qwen-Edit).
    #   - distilled (DEFAULT): -Fast-equivalent, 4 steps / CFG 1.
    #   - base (QUALITY): high-CFG variant, 20 steps / CFG 5.
    # ONLY the two CHECKPOINTS are pre-staged here. SwarmUI AUTO-FETCHES the FLUX.2 text encoder (Qwen3-4B) and
    # the FLUX.2 16ch VAE on first FLUX.2 use, so we deliberately DON'T download them as separate components:
    # a separate qwen_3_4b.safetensors into Models/text_encoders would collide with the qwen_3_4b TE Z-Image
    # already auto-fetches (shared filename -> whichever landed second silently SHADOWS the other, an order-
    # dependent cross-model breakage). Letting SwarmUI manage the shared TE/VAE avoids that entirely.
    $flux2 = "https://huggingface.co/Comfy-Org/flux2-klein-4B/resolve/main/split_files"
    Get-Model "$flux2/diffusion_models/flux-2-klein-4b.safetensors"      (Join-Path $diff "flux-2-klein-4b.safetensors")
    Get-Model "$flux2/diffusion_models/flux-2-klein-base-4b.safetensors" (Join-Path $diff "flux-2-klein-base-4b.safetensors")
    # FLUX.2 Klein 4B NVFP4 (~2.46GB): BFL's OWN NATIVE FP4 checkpoint — NOT a Nunchaku svdq quant. BFL's model
    # card cites native ComfyUI + Diffusers FP4 with NO Nunchaku/SVDQuant, and nunchaku's changelog has zero FLUX.2
    # entries, so it loads via ComfyUI's native FLUX.2 FP4 path and needs NO nunchaku wheel/node (hence it lives
    # here under -Models full, NOT the gated -Nunchaku block). The nvfp4 repo is BFL's OWN and is NON-GATED
    # (302->xet CDN, no 401 — unlike BFL's gated base FLUX.2-klein repo): the ONLY place a black-forest-labs resolve
    # URL is permitted (the plain Klein checkpoints above stay on the Comfy-Org repackage). It routes via the
    # existing 'flux-2-klein*' family override in doki-gen.ps1; with no '-base-' infix it takes the distilled band
    # (4 steps/cfg1/euler/Flux2) — a CONSERVATIVE, on-GPU-unverified call (BFL's nvfp4 card states no inference
    # config and doesn't label distilled-vs-base; the '-base-' convention was Comfy-Org's, not this repo). HEAD-verified.
    Get-Model "https://huggingface.co/black-forest-labs/FLUX.2-klein-4b-nvfp4/resolve/main/flux-2-klein-4b-nvfp4.safetensors" (Join-Path $diff "flux-2-klein-4b-nvfp4.safetensors")

    # Realism LoRA for `doki gen -Realism` — a photoreal Z-Image LoRA (Apache-2.0, public/non-gated HF
    # repo, scriptable resolve/ URL like the Wan-Lightning LoRAs above). Source ships the generic
    # pytorch_lora_weights.safetensors, so RENAME on save so it doesn't collide and SwarmUI references it
    # as <lora:Z-Image-Realism:0.7> (the exact base name doki-gen.ps1's Get-GenPromptFields emits). If this
    # download ever fails, drop any Z-Image realism .safetensors into Models\Lora named Z-Image-Realism.safetensors.
    Get-Model "https://huggingface.co/suayptalha/Z-Image-Turbo-Realism-LoRA/resolve/main/pytorch_lora_weights.safetensors" (Join-Path $loraD "Z-Image-Realism.safetensors")

    # Chroma — uncensored, FLUX-derived stylized complement. Use the *-final STABLE variant
    # (the repo's do_not_use/ files error in ComfyUI with a tensor mismatch).
    Get-Model "https://huggingface.co/silveroxides/Chroma1-HD-fp8-scaled/resolve/main/Chroma1-HD-fp8mixed-final.safetensors" (Join-Path $diff "Chroma1-HD-fp8mixed-final.safetensors")

    # Anime / illustration pair — open, uncensored SDXL specialists (OpenRAIL++), ~6.94GB each, trivial on
    # 32GB. Drop into Models\diffusion_models so they auto-appear in SwarmUI's checkpoint picker on install
    # (and are usable immediately via  doki gen -Model <file>  — the override rides the existing picker, no
    # recipe/C# change). RENAME to a stable local name (the Animagine repo also ships an *-opt variant; we
    # take the canonical base checkpoint, NOT -opt). HF-tree-verified single full checkpoints (no split_files).
    #   - Illustrious-XL v1.0: native 1536px anime SDXL, Danbooru-tag conditioning.
    #   - Animagine XL 4.0: anime SDXL 1.0 finetune.
    Get-Model "https://huggingface.co/OnomaAIResearch/Illustrious-XL-v1.0/resolve/main/Illustrious-XL-v1.0.safetensors" (Join-Path $diff "Illustrious-XL-v1.0.safetensors")
    Get-Model "https://huggingface.co/cagliostrolab/animagine-xl-4.0/resolve/main/animagine-xl-4.0.safetensors"         (Join-Path $diff "Animagine-XL-4.0.safetensors")

    # Upscaler: 4x-UltraSharp (ESRGAN) -> Models\upscale_models. SwarmUI exposes it as the
    # Upscale / Refiner-Upscale step for higher-detail stills and video frames.
    $upsc = Join-Path $swarm "Models\upscale_models"; New-Item -ItemType Directory -Force $upsc | Out-Null
    Get-Model "https://huggingface.co/Kim2091/UltraSharp/resolve/main/4x-UltraSharp.pth" (Join-Path $upsc "4x-UltraSharp.pth")

    # Precise image editing: Qwen-Image-Edit-2511 — SwarmUI-native instruction edit + free
    # inpaint. fp8mixed (~20GB) fits 32GB; Qwen2.5-VL TE + VAE shared from the Qwen-Image repo.
    # (2511 ships fp8mixed, NOT fp8_e4m3fn — HF-tree-verified.)
    $qedit = "https://huggingface.co/Comfy-Org/Qwen-Image-Edit_ComfyUI/resolve/main/split_files"
    $qbase = "https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files"
    Get-Model "$qedit/diffusion_models/qwen_image_edit_2511_fp8mixed.safetensors" (Join-Path $diff "qwen_image_edit_2511_fp8mixed.safetensors")
    Get-Model "$qbase/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors"        (Join-Path $te   "qwen_2.5_vl_7b_fp8_scaled.safetensors")
    Get-Model "$qbase/vae/qwen_image_vae.safetensors"                             (Join-Path $vae  "qwen_image_vae.safetensors")

    # Qwen-Image BASE (strong in-image TEXT) — the NON-distilled t2i unet as a QuantStack Q4_K_M GGUF
    # (~13.1GB). SwarmUI auto-detects the GGUF arch; on first GGUF load it prompts a one-time install-support
    # popup (autoinstalls city96/ComfyUI-GGUF), accepted headlessly via the existing InstallConfirmWS path
    # (5e). This is a DIFFERENT model from Edit-2511 above; it REUSES the Qwen2.5-VL TE + Qwen-Image VAE already
    # pulled by the two lines above ($te/$vae), so ONLY the unet is new (do NOT pull the GGUF repo's redundant
    # 254MB VAE). GATED on-GPU (render-unverified at rest): the GGUF arch detect + node popup + live 32GB fit.
    $qimggguf = "https://huggingface.co/QuantStack/Qwen-Image-GGUF/resolve/main"
    Get-Model "$qimggguf/Qwen_Image-Q4_K_M.gguf"                                  (Join-Path $diff "Qwen_Image-Q4_K_M.gguf")

    # Music / song generation: ACE-Step 1.5 — SwarmUI-NATIVE audio model (class AceStep15).
    # XL base = max quality, turbo = fast preset. The qwen ace15 text-encoders auto-download
    # on first gen; the ace 1.5 VAE is provided here. (ACE-Step 1.5, NOT the v1 all-in-one.)
    $ace = "https://huggingface.co/Comfy-Org/ace_step_1.5_ComfyUI_files/resolve/main/split_files"
    Get-Model "$ace/diffusion_models/acestep_v1.5_xl_base_bf16.safetensors" (Join-Path $diff "acestep_v1.5_xl_base_bf16.safetensors")
    Get-Model "$ace/diffusion_models/acestep_v1.5_turbo.safetensors"        (Join-Path $diff "acestep_v1.5_turbo.safetensors")
    Get-Model "$ace/vae/ace_1.5_vae.safetensors"                            (Join-Path $vae  "ace_1.5_vae.safetensors")

    # Fast video: LTXV-2b-0.9.8-distilled — SwarmUI-native (class lightricks-ltx-video),
    # near-real-time + long clips (up to ~257 frames). The T5 text-encoder auto-downloads on
    # first gen. A SPEED option below the slower, higher-quality Wan 2.2.
    Get-Model "https://huggingface.co/Lightricks/LTX-Video/resolve/main/ltxv-2b-0.9.8-distilled.safetensors" (Join-Path $diff "ltxv-2b-0.9.8-distilled.safetensors")

    # Audio (V2A): HunyuanVideo-Foley — adds synced sound to a silent clip (muxed by the
    # WanFoley custom workflow). fp16 main for max quality. CLAP + SigLIP2 encoders auto-
    # download on first run. License: Tencent Hunyuan Community (local/personal use OK).
    $fol = "https://huggingface.co/phazei/HunyuanVideo-Foley/resolve/main"
    Get-Model "$fol/hunyuanvideo_foley.safetensors"          (Join-Path $foley "hunyuanvideo_foley.safetensors")
    Get-Model "$fol/synchformer_state_dict_fp16.safetensors" (Join-Path $foley "synchformer_state_dict_fp16.safetensors")
    Get-Model "$fol/vae_128d_48k_fp16.safetensors"           (Join-Path $foley "vae_128d_48k_fp16.safetensors")

    # Prompt-rewriter LLM (the simple-prompt centerpiece) → repo models\ dir (served on :8013).
    Get-Model "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q5_K_M.gguf" (Join-Path $modelsDir "Qwen2.5-3B-Instruct-Q5_K_M.gguf")
}

# 5g. refresh model list
try {
    $sid2 = (Invoke-RestMethod "http://127.0.0.1:7801/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
    Invoke-RestMethod "http://127.0.0.1:7801/API/TriggerRefresh" -Method Post -Body (@{ session_id = $sid2 } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
} catch {}

# 5h. audio (full tier): HunyuanVideo-Foley ComfyUI node + the WanFoley custom workflow.
#     Models were fetched to Models\foley in 5f; CLAP + SigLIP2 encoders auto-download on first gen.
if ($Models -eq "full") {
    $nodes = Join-Path $swarm "dlbackend\comfy\ComfyUI\custom_nodes"
    if (Test-Path $nodes) {
        $foleyNode = Join-Path $nodes "ComfyUI-HunyuanVideo-Foley"
        if (-not (Test-Path $foleyNode)) { Info "installing HunyuanVideo-Foley node ..."; Git-Clone https://github.com/phazei/ComfyUI-HunyuanVideo-Foley $foleyNode } else { Ok "Foley node present" }
        $cpy = @("dlbackend\comfy\python_embeded\python.exe", "dlbackend\comfy\venv\Scripts\python.exe", "dlbackend\comfy\ComfyUI\venv\Scripts\python.exe") |
            ForEach-Object { Join-Path $swarm $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        $req = Join-Path $foleyNode "requirements.txt"
        if ($cpy -and (Test-Path $req)) { Info "installing Foley python deps ..."; & $cpy -m pip install -r $req | Out-Null; Ok "Foley deps installed" }
        else { Warn "Foley deps: run  <comfy-python> -m pip install -r `"$req`"  manually" }
    } else { Warn "ComfyUI backend not found yet; re-run setup.ps1 -Media -Models full after it installs" }

    # WanFoley custom workflow (Wan 2.2 -> Foley -> muxed MP4), authored once and committed to the repo.
    $wf  = Join-Path $root "media-assets\WanFoley.json"
    $cwf = Join-Path $swarm "src\BuiltinExtensions\ComfyUIBackend\CustomWorkflows\WanFoley.json"
    if (Test-Path $wf) { New-Item -ItemType Directory -Force (Split-Path $cwf) | Out-Null; Copy-Item $wf $cwf -Force; Ok "WanFoley workflow installed" }
    else { Warn "media-assets\WanFoley.json not present (authored at build time); skipping" }
}

# 5h-bis. InstantID face-identity reference (GATED sidecar, -FaceId) — SDXL-based, so it REUSES the anime
#     Illustrious/Animagine SDXL checkpoints already on disk (5f, lines ~549-550): NO new base download.
#     (PuLID-Flux was the alternative but needs FLUX.1-dev ~22GB which DokiDex does not ship — deferred until a
#     FLUX base tier exists.) Node = cubiq/ComfyUI_InstantID (maintenance-mode/stable). ~4.55GB of add-on weights.
#     Mirrors the Foley install (node clone + comfy-python pip + Get-Model weights). WORKFLOW IS NOT SHIPPED:
#     the upstream example (examples/InstantID_basic.json) is a ComfyUI UI-GRAPH export (not SwarmUI's API-prompt
#     CustomWorkflows format) and hardcodes the checkpoint + a reference jpg — converting/re-authoring it blind
#     would be guesswork, so the runnable InstantID.json is the ON-GPU authoring step (see docs/decisions.md).
#     Until media-assets\InstantID.json is authored on a live GPU, this installs node+weights only and Warns that
#     the workflow is absent (same Test-Path posture as the Foley copy above).
if ($FaceId) {
    Info "InstantID face-identity reference (cubiq/ComfyUI_InstantID, SDXL — reuses the anime base)"
    Ensure-WinGet "Git.Git" "git"
    $nodes = Join-Path $swarm "dlbackend\comfy\ComfyUI\custom_nodes"
    if (Test-Path $nodes) {
        # 1) the node
        $idNode = Join-Path $nodes "ComfyUI_InstantID"
        if (-not (Test-Path $idNode)) { Info "installing InstantID node ..."; Git-Clone https://github.com/cubiq/ComfyUI_InstantID $idNode } else { Ok "InstantID node present" }
        # 2) its python deps (same 3-candidate comfy-python probe the Foley node uses). onnxruntime-gpu ONLY
        #    (NOT plain onnxruntime too): with BOTH installed the CPU build can win the `onnxruntime` module
        #    namespace and CUDAExecutionProvider silently fails to register, so antelopev2 face-embedding falls
        #    back to slow CPU — a known InsightFace gotcha. The -gpu wheel provides the same module name with CUDA.
        if ($cpy) { Info "installing InstantID python deps (insightface + onnxruntime-gpu) ..."; & $cpy -m pip install insightface onnxruntime-gpu | Out-Null; Ok "InstantID deps installed" }
        else { Warn "InstantID deps: run  <comfy-python> -m pip install insightface onnxruntime-gpu  manually" }

        # 3) weights (~4.55GB): IP-Adapter (instantid\), ControlNet + config (controlnet\), antelopev2 face encoder.
        #    The InstantID node also CAN auto-download antelopev2 on first run; the explicit fetch below is a
        #    convenience (community HF mirror) — if it fails the node self-heals on first gen.
        $cmodels = Join-Path $swarm "dlbackend\comfy\ComfyUI\models"
        $idDir   = Join-Path $cmodels "instantid";  New-Item -ItemType Directory -Force $idDir   | Out-Null
        $cnDir   = Join-Path $cmodels "controlnet"; New-Item -ItemType Directory -Force $cnDir   | Out-Null
        $insDir  = Join-Path $cmodels "insightface\models\antelopev2"; New-Item -ItemType Directory -Force $insDir | Out-Null
        $ix = "https://huggingface.co/InstantX/InstantID/resolve/main"
        Get-Model "$ix/ip-adapter.bin"                                       (Join-Path $idDir "ip-adapter.bin")
        Get-Model "$ix/ControlNetModel/diffusion_pytorch_model.safetensors"  (Join-Path $cnDir "instantid_controlnet.safetensors")
        Get-Model "$ix/ControlNetModel/config.json"                          (Join-Path $cnDir "instantid_controlnet_config.json")
        # antelopev2 face encoder: a 361MB zip (upstream's primary pointer is Google Drive = not scriptable; this
        # is the MonsterMMORPG HF mirror). Unzip to insightface\models\antelopev2\ (the 5 .onnx models — a MIX, not
        # all detectors: glintr100 = recognition/embedding, genderage = attribute, scrfd/det = detection). The
        # 5-.onnx contents are NOT byte-verified at rest — confirm on-GPU, or delete the dir to let the node
        # auto-download antelopev2 on first run.
        $anteZip = Join-Path $insDir "antelopev2.zip"
        $anteOk  = Join-Path $insDir "glintr100.onnx"   # sentinel: one of the 5 expected detectors
        if (-not (Test-Path $anteOk)) {
            Get-Model "https://huggingface.co/MonsterMMORPG/tools/resolve/main/antelopev2.zip" $anteZip
            if (Test-Path $anteZip) {
                Info "unzipping antelopev2 face encoder ..."
                try { Expand-Archive -Path $anteZip -DestinationPath $insDir -Force; Remove-Item $anteZip -Force -ErrorAction SilentlyContinue; Ok "antelopev2 unzipped -> $insDir" }
                catch { Warn "antelopev2 unzip failed ($($_.Exception.Message)); the InstantID node can auto-download it on first run instead" }
            } else { Warn "antelopev2 mirror unreachable; the InstantID node can auto-download it on first run instead" }
        } else { Ok "antelopev2 present" }
    } else { Warn "ComfyUI backend not found yet; re-run setup.ps1 -Media -FaceId after it installs" }

    # 4) workflow registration — GATED on the JSON existing. The runnable SwarmUI-API InstantID.json is NOT
    #    sourceable from the upstream UI-graph example (see the header comment) — it is the on-GPU authoring step.
    #    Until media-assets\InstantID.json is authored + validated on a live GPU, copy nothing and Warn (same
    #    Test-Path posture as the Foley copy). Once committed it rides the existing  doki gen -Workflow InstantID
    #    -InitImage <face.png>  hook (comfyuicustomworkflow=InstantID), no C#/recipe change.
    $idWf  = Join-Path $root "media-assets\InstantID.json"
    $idCwf = Join-Path $swarm "src\BuiltinExtensions\ComfyUIBackend\CustomWorkflows\InstantID.json"
    if (Test-Path $idWf) { New-Item -ItemType Directory -Force (Split-Path $idCwf) | Out-Null; Copy-Item $idWf $idCwf -Force; Ok "InstantID workflow installed" }
    else { Warn "media-assets\InstantID.json not present (the runnable SwarmUI workflow is the on-GPU authoring step — see docs/decisions.md); node + weights installed, workflow skipped" }

    Ok "InstantID ready -> node + weights installed. Run via  doki gen -FaceId -InitImage <face.png> '<prompt>'  (or the lower-level  doki gen -Workflow InstantID -InitImage <face.png>) once the workflow JSON is authored on-GPU."
}

# 5h-ter. InfiniteTalk audio-driven talking-video (GATED sidecar, -InfiniteTalk) — the REAL ComfyUI integration
#     is Kijai's WanVideoWrapper (NOT a standalone MeiGen node: the MeiGen `comfyui` branch is itself "based on
#     ComfyUI-WanVideoWrapper"). Mirrors the Foley/InstantID install (node clone + comfy-python pip + Get-Model
#     weights), but is MUCH heavier than InstantID in TWO ways flagged here exactly like InstantID flags PuLID's
#     missing FLUX base:
#       (1) THE ~82GB BLOCKER — InfiniteTalk's adapter injects into the Wan2.1-I2V-**14B** UNet specifically.
#           DokiDex ships Wan **2.2** (5B ti2v + A14B-T2V GGUF + VAEs, 5f ~lines 490-510) but NOT the Wan2.1
#           I2V-14B base, and the 2.2 models do NOT substitute (different arch). So -InfiniteTalk pulls an ~82GB
#           NEW base (diffusion shards + UMT5-xxl TE + open-clip ViT-H + VAE) that DWARFS every other DokiDex
#           weight. An fp8/GGUF Wan2.1-I2V-14B repack would shrink this but must be sourced/verified ON-GPU
#           (the Kijai wrapper supports fp8 Wan I2V bases; that exact file was not verifiable at rest).
#       (2) 32GB FIT IS UNCONFIRMED — InfiniteTalk rides the FULL 14B base (worse than every existing DokiDex
#           video path: i2v is Wan2.2-5B, Foley rides the lighter Wan2.2-5B fp16 base). Native fp16 14B OOMs on
#           32GB; the feasible path
#           (fp8 base + block-swap/StepSwap offload + 81-frame/25-overlap chunking) is plausible but NOT
#           guaranteed and can only be settled by a live render. Treat the WHOLE feature as on-GPU-gated.
#     WORKFLOW IS NOT SHIPPED: the only example_workflows on Kijai's repo
#     (wanvideo_2_1_14B_I2V_InfiniteTalk_example_03.json etc.) are ComfyUI UI-GRAPH exports (top-level
#     id/nodes/links/groups), NOT SwarmUI's flat API-prompt CustomWorkflows format; the MeiGen examples/ JSONs are
#     CLI inference configs, not ComfyUI workflows at all. So the runnable media-assets\InfiniteTalk.json is the
#     ON-GPU authoring step (load the UI-graph live -> convert to API-prompt -> rewire base/wav2vec/adapter paths
#     -> wire the image+audio inputs to SwarmUI's injection points; see docs/decisions.md). Until then this installs
#     node+weights only and Warns the workflow is absent (same Test-Path posture as the Foley/InstantID copies).
#     Cite: github.com/kijai/ComfyUI-WanVideoWrapper/tree/main/example_workflows ; github.com/MeiGen-AI/InfiniteTalk/tree/comfyui.
if ($InfiniteTalk) {
    Info "InfiniteTalk audio-driven talking-video (Kijai/ComfyUI-WanVideoWrapper) — NOTE pulls an ~82GB Wan2.1-I2V-14B base NOT otherwise on disk; 32GB fit is on-GPU-unconfirmed"
    Ensure-WinGet "Git.Git" "git"
    $nodes = Join-Path $swarm "dlbackend\comfy\ComfyUI\custom_nodes"
    if (Test-Path $nodes) {
        # 1) the node (Kijai's WanVideoWrapper — the real InfiniteTalk/MultiTalk integration; heavier than
        #    InstantID: its requirements pull accelerate/sageattention etc.).
        $wvNode = Join-Path $nodes "ComfyUI-WanVideoWrapper"
        if (-not (Test-Path $wvNode)) { Info "installing WanVideoWrapper node ..."; Git-Clone https://github.com/kijai/ComfyUI-WanVideoWrapper $wvNode } else { Ok "WanVideoWrapper node present" }
        # 2) its python deps (same 3-candidate comfy-python probe Foley/InstantID use, with the $cpy/else Warn
        #    fallback). This requirements.txt is heavier than InstantID's (accelerate, sageattention, ...).
        $cpy = @("dlbackend\comfy\python_embeded\python.exe", "dlbackend\comfy\venv\Scripts\python.exe", "dlbackend\comfy\ComfyUI\venv\Scripts\python.exe") |
            ForEach-Object { Join-Path $swarm $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        $wvReq = Join-Path $wvNode "requirements.txt"
        if ($cpy -and (Test-Path $wvReq)) { Info "installing WanVideoWrapper python deps (accelerate/sageattention/...) ..."; & $cpy -m pip install -r $wvReq | Out-Null; Ok "WanVideoWrapper deps installed" }
        else { Warn "WanVideoWrapper deps: run  <comfy-python> -m pip install -r `"$wvReq`"  manually" }

        $cmodels = Join-Path $swarm "dlbackend\comfy\ComfyUI\models"

        # 3A) the InfiniteTalk ADAPTER (the only genuinely new SMALL file) — Kijai's repackaged fp16 single-file
        #     form (the official MeiGen-AI/InfiniteTalk repo is a 169GB full tree, NOT pullable whole). NOTE the
        #     upstream typo "InfiniTetalk" in the single filename is REAL — copied byte-for-byte. No fp8 variant
        #     exists on Kijai's tree (fp16 only). -> diffusion_models.
        #     The default -InfiniteTalk path is SINGLE-portrait (the `doki gen -InfiniteTalk -InitImage <portrait>`
        #     hook drives ONE speaker), so we fetch ONLY the Single adapter. The ~5.12GB Multi-person adapter
        #     (Wan2_1-InfiniteTalk-Multi_fp16.safetensors, same $itk tree) is a separate MANUAL add for the
        #     multi-speaker case — drop it in $itDir yourself if you need it; the default install won't pull
        #     5GB the single-portrait wiring never references.
        $itDir = Join-Path $cmodels "diffusion_models"; New-Item -ItemType Directory -Force $itDir | Out-Null
        $itk = "https://huggingface.co/Kijai/WanVideo_comfy/resolve/main/InfiniteTalk"
        Get-Model "$itk/Wan2_1-InfiniTetalk-Single_fp16.safetensors" (Join-Path $itDir "Wan2_1-InfiniTetalk-Single_fp16.safetensors")  # 5.13 GB (the upstream "InfiniTetalk" typo is intentional)

        # 3B) the audio encoder chinese-wav2vec2-base (TencentGameMate, HF/transformers path — the .bin works for
        #     the transformers loader; the 1.14GB fairseq .pt form is NOT needed, skipped). NOTE: Kijai's wrapper
        #     sometimes wants the PR-revision safetensors (refs/pr/1 model.safetensors) — confirm ON-GPU which the
        #     installed wrapper version expects. -> Models\wav2vec2\chinese-wav2vec2-base.
        $w2vDir = Join-Path $cmodels "wav2vec2\chinese-wav2vec2-base"; New-Item -ItemType Directory -Force $w2vDir | Out-Null
        $w2v = "https://huggingface.co/TencentGameMate/chinese-wav2vec2-base/resolve/main"
        Get-Model "$w2v/pytorch_model.bin"           (Join-Path $w2vDir "pytorch_model.bin")            # 380 MB
        Get-Model "$w2v/config.json"                 (Join-Path $w2vDir "config.json")                  # ~2 KB
        Get-Model "$w2v/preprocessor_config.json"    (Join-Path $w2vDir "preprocessor_config.json")     # 160 B

        # 3C) the BASE — Wan2.1-I2V-14B-480P — THE ~82GB BLOCKER, NOT on disk (DokiDex ships Wan 2.2, not this
        #     2.1 I2V-14B). InfiniteTalk's adapter injects into THIS specific 14B UNet; the on-disk 2.2 5B/A14B-T2V
        #     models do NOT substitute. Diffusion = 7 shards (~65.6GB) + UMT5-xxl TE (11.4GB) + open-clip ViT-H
        #     (4.77GB) + VAE (508MB). An fp8/GGUF repack would shrink this but must be sourced/verified ON-GPU.
        #     -> diffusion_models (shards) + the wrapper-expected TE/clip/VAE dirs.
        # ON-GPU PATH-ROUTING CONFIRM: these base TE/clip-vision/VAE files land under the RAW ComfyUI backend tree
        # ($cmodels = dlbackend\comfy\ComfyUI\models\...), NOT SwarmUI's own Models\... where DokiDex's other media
        # weights live. Whether SwarmUI bridges those two folder namespaces for the WanVideoWrapper node is on-GPU
        # (the wrapper may specifically resolve against the ComfyUI tree via its own folder_paths — like the Foley
        # node does — so the placement is left here deliberately; confirm it resolves once the workflow is authored).
        $teDir  = Join-Path $cmodels "text_encoders";  New-Item -ItemType Directory -Force $teDir  | Out-Null
        $clipDir= Join-Path $cmodels "clip_vision";    New-Item -ItemType Directory -Force $clipDir| Out-Null
        $vaeDir = Join-Path $cmodels "vae";            New-Item -ItemType Directory -Force $vaeDir | Out-Null
        $w21 = "https://huggingface.co/Wan-AI/Wan2.1-I2V-14B-480P/resolve/main"
        # the 7 diffusion shards (~65.6 GB) — unrolled to explicit literal URLs/filenames (not a -f loop) so each
        # rides the standard Get-Model atomic .part-promote and lands a UNIQUE local name (the dupe-guard checks).
        Get-Model "$w21/diffusion_pytorch_model-00001-of-00007.safetensors" (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00001-of-00007.safetensors")
        Get-Model "$w21/diffusion_pytorch_model-00002-of-00007.safetensors" (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00002-of-00007.safetensors")
        Get-Model "$w21/diffusion_pytorch_model-00003-of-00007.safetensors" (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00003-of-00007.safetensors")
        Get-Model "$w21/diffusion_pytorch_model-00004-of-00007.safetensors" (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00004-of-00007.safetensors")
        Get-Model "$w21/diffusion_pytorch_model-00005-of-00007.safetensors" (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00005-of-00007.safetensors")
        Get-Model "$w21/diffusion_pytorch_model-00006-of-00007.safetensors" (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00006-of-00007.safetensors")
        Get-Model "$w21/diffusion_pytorch_model-00007-of-00007.safetensors" (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00007-of-00007.safetensors")
        Get-Model "$w21/diffusion_pytorch_model.safetensors.index.json"                       (Join-Path $itDir "Wan2_1-I2V-14B-480P_diffusion_pytorch_model.safetensors.index.json")
        Get-Model "$w21/models_t5_umt5-xxl-enc-bf16.pth"                                       (Join-Path $teDir "Wan2_1_umt5-xxl-enc-bf16.pth")        # 11.4 GB
        Get-Model "$w21/models_clip_open-clip-xlm-roberta-large-vit-huge-14.pth"               (Join-Path $clipDir "Wan2_1_open-clip-xlm-roberta-large-vit-huge-14.pth")  # 4.77 GB
        Get-Model "$w21/Wan2.1_VAE.pth"                                                        (Join-Path $vaeDir "Wan2_1_VAE.pth")                     # 508 MB
    } else { Warn "ComfyUI backend not found yet; re-run setup.ps1 -Media -InfiniteTalk after it installs" }

    # 4) workflow registration — GATED on the JSON existing. No authoritative SwarmUI-API InfiniteTalk.json is
    #    sourceable (Kijai's example_workflows are UI-graphs; MeiGen's examples/ are CLI configs) — it is the
    #    on-GPU authoring step (see the header comment + docs/decisions.md). Until media-assets\InfiniteTalk.json
    #    is authored + validated on a live GPU, copy nothing and Warn (same Test-Path posture as Foley/InstantID).
    #    Once committed it rides the existing  doki gen -InfiniteTalk -InitImage <portrait> -Audio <clip>  hook
    #    (comfyuicustomworkflow=InfiniteTalk), no C#/recipe change.
    $itWf  = Join-Path $root "media-assets\InfiniteTalk.json"
    $itCwf = Join-Path $swarm "src\BuiltinExtensions\ComfyUIBackend\CustomWorkflows\InfiniteTalk.json"
    if (Test-Path $itWf) { New-Item -ItemType Directory -Force (Split-Path $itCwf) | Out-Null; Copy-Item $itWf $itCwf -Force; Ok "InfiniteTalk workflow installed" }
    else { Warn "media-assets\InfiniteTalk.json not present (the runnable SwarmUI workflow is the on-GPU authoring step — see docs/decisions.md); node + weights installed, workflow skipped" }

    Ok "InfiniteTalk ready -> node + weights installed (~82GB Wan2.1-I2V-14B base + adapter + wav2vec2). On-GPU LABELED: author the workflow JSON, confirm the 32GB fit (fp8 base + block-swap), source the fp8 repack, and pin the audio body-key. Run via  doki gen -InfiniteTalk -InitImage <portrait> -Audio <clip> '<prompt>'  once authored."
}

# 5h-quater. LatentSync — the LIGHT lip-sync (GATED sidecar, -LatentSync). The LIGHTER alternative to InfiniteTalk
#     (5h-ter): ByteDance LatentSync 1.5 fits 8GB VRAM / ~9.5GB on disk — roughly 1/9th of InfiniteTalk's ~82GB —
#     and is commercially licensed (weights OpenRAIL++, code Apache-2.0). The maintained ComfyUI integration is
#     ShmuelRonen/ComfyUI-LatentSyncWrapper (951 stars, active through Sept 2025, tracks upstream 1.5->1.6). This
#     mirrors the Foley/InstantID/InfiniteTalk install EXACTLY (node clone + comfy-python pip + Get-Model weights +
#     Test-Path workflow-copy-or-Warn), with TWO honest divergences flagged here:
#       (1) I/O DIVERGENCE — LatentSync is a VIDEO-to-video lip RE-SYNC (it edits an existing clip's mouth to new
#           audio), NOT a portrait->talking-video generator like InfiniteTalk. So `doki gen -LatentSync -Audio
#           <clip>` requires the driving voice ONLY; the source video rides the workflow's own video-input channel
#           (no mandatory portrait -InitImage). It is ADDITIVE — it does NOT obsolete InfiniteTalk (different job).
#       (2) NO SHARING with InfiniteTalk — LatentSync's audio encoder is Whisper-tiny (whisper/tiny.pt, 75.6MB), a
#           DIFFERENT model than InfiniteTalk's chinese-wav2vec2; and it uses s3fd/2DFAN4 for face detect/landmarks,
#           NOT antelopev2. It is a self-contained SD-VAE-latent model with ZERO Wan dependency — the ~9.8GB is
#           all-new but TINY, and there is no meaningful re-download to avoid.
#     WEIGHTS ride the PUBLIC ByteDance/LatentSync-1.5 repo (OpenRAIL++). The 1.6 repo is intermittently gated/
#     private per the wrapper README, so 1.5 is the safe default (fits 8GB, leaves max 32GB headroom). The model is
#     an SD-VAE-latent diffusion model, so it CANNOT run without the SD-VAE (stabilityai/sd-vae-ft-mse) — the wrapper
#     README flags it as a REQUIRED manual download into checkpoints/vae/, so we fetch it (+ the repo-root config.json)
#     alongside the core runtime + the auxiliaries. The wrapper also lazy-pulls some weights on first run.
#     WORKFLOW IS NOT SHIPPED: the wrapper's example_workflows/ are ComfyUI UI-GRAPH exports (top-level
#     id/nodes/links/groups), NOT SwarmUI's flat API-prompt CustomWorkflows format — identical to the InfiniteTalk/
#     PuLID/TtsSuite blocker. So the runnable media-assets\LatentSync.json is the ON-GPU authoring step (load the
#     UI-graph live -> convert to API-prompt -> rewire the checkpoint/whisper/syncnet paths -> wire SwarmUI's
#     video-input + audio-load injection points -> validate a render; see docs/decisions.md). Until then this
#     installs node+weights only and Warns the workflow is absent (same Test-Path posture as Foley/InstantID/
#     InfiniteTalk). Cite: github.com/ShmuelRonen/ComfyUI-LatentSyncWrapper ; huggingface.co/ByteDance/LatentSync-1.5.
if ($LatentSync) {
    Info "LatentSync LIGHT lip-sync (ShmuelRonen/ComfyUI-LatentSyncWrapper) — ByteDance LatentSync 1.5, ~9.8GB / 8GB VRAM (the LIGHTER alternative to the ~82GB InfiniteTalk; video-in re-sync, not portrait->video)"
    Ensure-WinGet "Git.Git" "git"
    $nodes = Join-Path $swarm "dlbackend\comfy\ComfyUI\custom_nodes"
    if (Test-Path $nodes) {
        # 1) the node (ShmuelRonen's LatentSyncWrapper — the maintained wrapper running current 1.5/1.6 weights).
        $lsNode = Join-Path $nodes "ComfyUI-LatentSyncWrapper"
        if (-not (Test-Path $lsNode)) { Info "installing LatentSyncWrapper node ..."; Git-Clone https://github.com/ShmuelRonen/ComfyUI-LatentSyncWrapper $lsNode } else { Ok "LatentSyncWrapper node present" }
        # 2) its python deps (same 3-candidate comfy-python probe Foley/InstantID/InfiniteTalk use, with the
        #    $cpy/else Warn graceful fallback).
        $cpy = @("dlbackend\comfy\python_embeded\python.exe", "dlbackend\comfy\venv\Scripts\python.exe", "dlbackend\comfy\ComfyUI\venv\Scripts\python.exe") |
            ForEach-Object { Join-Path $swarm $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        $lsReq = Join-Path $lsNode "requirements.txt"
        if ($cpy -and (Test-Path $lsReq)) { Info "installing LatentSyncWrapper python deps ..."; & $cpy -m pip install -r $lsReq | Out-Null; Ok "LatentSyncWrapper deps installed" }
        else { Warn "LatentSyncWrapper deps: run  <comfy-python> -m pip install -r `"$lsReq`"  manually" }

        # 3) the weights — the PUBLIC ByteDance/LatentSync-1.5 tree (OpenRAIL++) + the wrapper's REQUIRED SD-VAE. The
        #    wrapper README documents an EXACT checkpoints tree under the node's own dir: checkpoints/{latentsync_unet.pt,
        #    stable_syncnet.pt, config.json}, checkpoints/whisper/tiny.pt, checkpoints/vae/{diffusion_pytorch_model.safetensors,
        #    config.json}, and checkpoints/auxiliary/ — so vae/, whisper/, auxiliary/ are all siblings directly under
        #    checkpoints/ (README-verified, not the ComfyUI models tree). The one path the README does NOT enumerate is
        #    the CONTENTS of checkpoints/auxiliary/ (it shows the folder bare): the 8 aux filenames + that exact relative
        #    layout are taken from the LatentSync-1.5 repo's own auxiliary/ tree and are the labeled on-GPU confirm here
        #    (auxiliary/ filenames vs whatever the node's first-run actually reads) — NOT the checkpoints-root layout,
        #    which IS documented.
        $ckptDir = Join-Path $lsNode "checkpoints";          New-Item -ItemType Directory -Force $ckptDir | Out-Null
        $whDir   = Join-Path $ckptDir "whisper";             New-Item -ItemType Directory -Force $whDir   | Out-Null
        $vaeDir  = Join-Path $ckptDir "vae";                 New-Item -ItemType Directory -Force $vaeDir  | Out-Null
        $auxDir  = Join-Path $ckptDir "auxiliary";           New-Item -ItemType Directory -Force $auxDir  | Out-Null
        $ls = "https://huggingface.co/ByteDance/LatentSync-1.5/resolve/main"
        # 3A) CORE RUNTIME (~6.8GB) — the diffusion UNet + SyncNet supervision + the Whisper-tiny audio encoder + the
        #     repo-root config.json (the LatentSync model config the wrapper loads from checkpoints/config.json).
        Get-Model "$ls/latentsync_unet.pt"  (Join-Path $ckptDir "latentsync_unet.pt")   # 5.07 GB (the diffusion UNet)
        Get-Model "$ls/stable_syncnet.pt"   (Join-Path $ckptDir "stable_syncnet.pt")    # 1.61 GB (SyncNet supervision)
        Get-Model "$ls/whisper/tiny.pt"     (Join-Path $whDir   "tiny.pt")              # 75.6 MB (Whisper-tiny audio encoder)
        Get-Model "$ls/config.json"         (Join-Path $ckptDir "config.json")          # 32 B  (the LatentSync-1.5 model config, checkpoints/config.json)
        # 3B) THE SD-VAE (~335MB) — REQUIRED. LatentSync is an SD-VAE-LATENT diffusion model: it encodes/decodes
        #     frames through stabilityai/sd-vae-ft-mse, so it CANNOT run without this. The wrapper README flags it as
        #     a manual download into checkpoints/vae/ (diffusion_pytorch_model.safetensors + config.json). UNGATED
        #     (resolve 302s to a public xet CDN, HEAD-verified).
        $sdvae = "https://huggingface.co/stabilityai/sd-vae-ft-mse/resolve/main"
        Get-Model "$sdvae/diffusion_pytorch_model.safetensors" (Join-Path $vaeDir "diffusion_pytorch_model.safetensors")  # 335 MB (the SD-VAE weights)
        Get-Model "$sdvae/config.json"                         (Join-Path $vaeDir "config.json")                         # 547 B (the SD-VAE config)
        # 3C) AUXILIARY face/quality weights (~3GB) — the EXHAUSTIVE 8-file set from the LatentSync-1.5 auxiliary/ tree
        #     (the wrapper may also lazy-pull on first run). s3fd/sfd = face detect, 2DFAN4 = landmarks, vit_g/koniq/
        #     vgg16 = quality scoring, syncnet_v2/i3d = sync/temporal.
        Get-Model "$ls/auxiliary/vit_g_hybrid_pt_1200e_ssv2_ft.pth" (Join-Path $auxDir "vit_g_hybrid_pt_1200e_ssv2_ft.pth")  # 2.02 GB
        Get-Model "$ls/auxiliary/vgg16-397923af.pth"                (Join-Path $auxDir "vgg16-397923af.pth")                 # 0.55 GB
        Get-Model "$ls/auxiliary/s3fd-619a316812.pth"               (Join-Path $auxDir "s3fd-619a316812.pth")                # 0.09 GB (face detect)
        Get-Model "$ls/auxiliary/sfd_face.pth"                      (Join-Path $auxDir "sfd_face.pth")                       # 0.09 GB
        # 2DFAN4 is INTENTIONALLY left as a .zip — the face-alignment lib consumes the .zip directly; do NOT Expand-Archive it.
        Get-Model "$ls/auxiliary/2DFAN4-cd938726ad.zip"             (Join-Path $auxDir "2DFAN4-cd938726ad.zip")              # 0.10 GB (landmarks; kept zipped)
        Get-Model "$ls/auxiliary/koniq_pretrained.pkl"              (Join-Path $auxDir "koniq_pretrained.pkl")               # 0.11 GB
        Get-Model "$ls/auxiliary/syncnet_v2.model"                  (Join-Path $auxDir "syncnet_v2.model")                   # 0.05 GB
        Get-Model "$ls/auxiliary/i3d_torchscript.pt"                (Join-Path $auxDir "i3d_torchscript.pt")                 # 0.05 GB
    } else { Warn "ComfyUI backend not found yet; re-run setup.ps1 -Media -LatentSync after it installs" }

    # 4) workflow registration — GATED on the JSON existing. No authoritative SwarmUI-API LatentSync.json is
    #    sourceable (the wrapper's example_workflows are UI-graphs) — it is the on-GPU authoring step (see the
    #    header comment + docs/decisions.md). Until media-assets\LatentSync.json is authored + validated on a live
    #    GPU, copy nothing and Warn (same Test-Path posture as Foley/InstantID/InfiniteTalk). Once committed it
    #    rides the existing  doki gen -LatentSync -Audio <clip>  hook (comfyuicustomworkflow=LatentSync), no
    #    C#/recipe change.
    $lsWf  = Join-Path $root "media-assets\LatentSync.json"
    $lsCwf = Join-Path $swarm "src\BuiltinExtensions\ComfyUIBackend\CustomWorkflows\LatentSync.json"
    if (Test-Path $lsWf) { New-Item -ItemType Directory -Force (Split-Path $lsCwf) | Out-Null; Copy-Item $lsWf $lsCwf -Force; Ok "LatentSync workflow installed" }
    else { Warn "media-assets\LatentSync.json not present (the runnable SwarmUI workflow is the on-GPU authoring step — see docs/decisions.md); node + weights installed, workflow skipped" }

    Ok "LatentSync ready -> node + weights installed (~9.8GB ByteDance LatentSync 1.5 incl. the required SD-VAE; fits 8GB VRAM). On-GPU LABELED: author the workflow JSON, confirm node-load, wire the video-input channel, and pin the audio body-key. Run via  doki gen -LatentSync -Audio <clip> '<prompt>'  once authored."
}

# 5h-quinquies. PuLID-Flux face-identity (GATED sidecar, -Pulid) — FLUX-based face-ID, the alternative InstantID
#     (5h-bis) deferred because DokiDex shipped no FLUX.1-dev base. THIS block un-blocks it via a NON-GATED FLUX
#     fp8 path (NOT the gated black-forest-labs / Comfy-Org repos): Kijai/flux-fp8 ships an ungated fp8 UNET + the
#     ungated bf16 ae, and comfyanonymous/flux_text_encoders ships the ungated t5xxl/clip_l — together ~17GB (11.9 unet + 4.9 t5 +
#     .25 clip_l + .17 vae), comfortably inside 32GB. (All HF-tree verified: the model cards show NO contact-info
#     gate; resolve URLs 302 to a public CDN, not a login wall. The license is still FLUX.1 [dev] Non-Commercial —
#     free/ungated download, commercial use restricted — the same posture DokiDex already accepted for dev-license
#     models.) Mirrors the Foley/InstantID install (node clone + comfy-python pip + Get-Model weights).
#     TWO honest caveats, flagged here exactly like InstantID flagged PuLID's missing base:
#       (1) THE NODE IS WEAK — balazik/ComfyUI-PuLID-Flux self-describes as "Alpha version V0.1.0 ... a prototype";
#           last commit 2024-10-03 (~20 months stale, predates the current ComfyUI/torch), so it may not LOAD on a
#           2026 ComfyUI without patching. The only fork (sipie800 Enhanced) is FORMALLY DISCONTINUED 2025-10-07 —
#           do NOT use it. So node-load is the labeled on-GPU step (verify it imports BEFORE authoring the workflow).
#       (2) ~17GB NEW FOOTPRINT — unlike InstantID's zero-new-base reuse, PuLID-Flux adds the FLUX fp8 unet + three
#           SEPARATE FLUX encoders (t5xxl + clip_l + ae) DokiDex did not previously ship (its bases are
#           Z-Image/SDXL/flux-2-klein, none provide FLUX.1's t5xxl). SwarmUI maps the ComfyUI dirs: the fp8 unet ->
#           diffusion_models\, the t5xxl/clip_l -> clip\, the ae -> vae\.
#     antelopev2 IS SHARED with InstantID (5h-bis): the balazik README target is EXACTLY the same
#     models\insightface\models\antelopev2 path the -FaceId block populates, so the glintr100.onnx sentinel below
#     SKIPS the download when -FaceId already ran (no re-download across the two paths). The PuLID-specific net-new
#     weight is just pulid_flux_v0.9.1 (~1.14GB) + the EVA02-CLIP the node auto-downloads on first run (~600MB).
#     WORKFLOW IS NOT SHIPPED: balazik's examples/ (pulid_flux_16bit_simple.json etc.) are ComfyUI UI-GRAPH exports
#     (top-level nodes/links/groups), NOT SwarmUI's flat API-prompt CustomWorkflows format; so the runnable
#     media-assets\PuLID.json is the ON-GPU authoring step (convert UI-graph -> API-prompt, repoint to the fp8 base
#     + the separate t5xxl/clip_l/ae, inject the SwarmUI ${prompt} + the reference-face init-image placeholder; see
#     docs/decisions.md). Until then this installs node+weights only and Warns the workflow is absent (same
#     Test-Path posture as the Foley/InstantID/InfiniteTalk copies). Cite:
#     github.com/balazik/ComfyUI-PuLID-Flux/tree/master/examples ; huggingface.co/Kijai/flux-fp8 ; huggingface.co/guozinan/PuLID.
if ($Pulid) {
    Info "PuLID-Flux face-identity (balazik/ComfyUI-PuLID-Flux, FLUX.1-dev) — pulls a NON-GATED ~17GB FLUX fp8 base (Kijai unet + t5xxl/clip_l/ae); node is Alpha/stale (node-load is the on-GPU step); SHARES InstantID's antelopev2"
    Ensure-WinGet "Git.Git" "git"
    $nodes = Join-Path $swarm "dlbackend\comfy\ComfyUI\custom_nodes"
    if (Test-Path $nodes) {
        # 1) the node (balazik/ComfyUI-PuLID-Flux — Alpha V0.1.0, last commit 2024-10-03; the only viable node, but
        #    INSTALL-ONLY: do NOT promise it loads on a current ComfyUI without the on-GPU load test). NOT the
        #    sipie800 Enhanced fork (formally discontinued 2025-10-07).
        $puNode = Join-Path $nodes "ComfyUI-PuLID-Flux"
        if (-not (Test-Path $puNode)) { Info "installing PuLID-Flux node ..."; Git-Clone https://github.com/balazik/ComfyUI-PuLID-Flux $puNode } else { Ok "PuLID-Flux node present" }
        # 2) its python deps (same 3-candidate comfy-python probe Foley/InstantID/InfiniteTalk use). onnxruntime-gpu
        #    ONLY (NOT plain onnxruntime too): the same EP-namespace clash documented in the InstantID block — with
        #    both present the CPU build can win the `onnxruntime` module namespace so CUDAExecutionProvider silently
        #    fails to register and antelopev2 falls back to slow CPU. insightface is SHARED with InstantID, so this
        #    is a no-op pip if -FaceId already ran.
        $cpy = @("dlbackend\comfy\python_embeded\python.exe", "dlbackend\comfy\venv\Scripts\python.exe", "dlbackend\comfy\ComfyUI\venv\Scripts\python.exe") |
            ForEach-Object { Join-Path $swarm $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($cpy) { Info "installing PuLID-Flux python deps (facexlib + insightface + onnxruntime-gpu) ..."; & $cpy -m pip install facexlib insightface onnxruntime-gpu | Out-Null; Ok "PuLID-Flux deps installed" }
        else { Warn "PuLID-Flux deps: run  <comfy-python> -m pip install facexlib insightface onnxruntime-gpu  manually" }

        $cmodels = Join-Path $swarm "dlbackend\comfy\ComfyUI\models"

        # 3) the NON-GATED FLUX fp8 base (~17GB) — Kijai's ungated fp8 UNET + ae + comfyanonymous's ungated t5xxl/clip_l.
        #    SwarmUI maps the ComfyUI dirs: fp8 unet -> diffusion_models\, t5xxl/clip_l -> clip\, ae -> vae\.
        #    (NOT black-forest-labs/FLUX.1-dev — that one IS hard-gated behind a license click-through, not scriptable;
        #    Comfy-Org/flux1-dev is license-restricted (FLUX.1 [dev] Non-Commercial) but technically scriptable, yet
        #    its full-precision all-in-one is a poor 32GB fit — Kijai's ~17GB fp8 footprint is the practical 32GB path.
        #    Kijai/flux-fp8 + comfyanonymous/flux_text_encoders are verified ungated.)
        $diffDir = Join-Path $cmodels "diffusion_models"; New-Item -ItemType Directory -Force $diffDir | Out-Null
        $clipDir = Join-Path $cmodels "clip";             New-Item -ItemType Directory -Force $clipDir | Out-Null
        $vaeDir  = Join-Path $cmodels "vae";              New-Item -ItemType Directory -Force $vaeDir  | Out-Null
        Get-Model "https://huggingface.co/Kijai/flux-fp8/resolve/main/flux1-dev-fp8.safetensors"                      (Join-Path $diffDir "flux1-dev-fp8.safetensors")          # 11.9 GB fp8_e4m3fn UNET (ungated)
        Get-Model "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/t5xxl_fp8_e4m3fn.safetensors" (Join-Path $clipDir "t5xxl_fp8_e4m3fn.safetensors")       # 4.9 GB FLUX T5 text-encoder (ungated)
        Get-Model "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/clip_l.safetensors"          (Join-Path $clipDir "clip_l.safetensors")                 # 246 MB CLIP-L (ungated)
        Get-Model "https://huggingface.co/Kijai/flux-fp8/resolve/main/flux-vae-bf16.safetensors"                      (Join-Path $vaeDir "flux-ae.safetensors")                 # 168 MB FLUX VAE (Kijai's bf16, ungated)

        # 3-bis) the PuLID-Flux model (guozinan/PuLID is NON-GATED) — the NEWER v0.9.1. -> models\pulid\. The
        #        EVA02-CLIP (EVA02_CLIP_L_336_psz14_s6B.pt) the node AUTO-downloads on first run (~600MB), so it is
        #        not pre-fetched here.
        $puDir = Join-Path $cmodels "pulid"; New-Item -ItemType Directory -Force $puDir | Out-Null
        Get-Model "https://huggingface.co/guozinan/PuLID/resolve/main/pulid_flux_v0.9.1.safetensors"                  (Join-Path $puDir "pulid_flux_v0.9.1.safetensors")        # 1.14 GB (ungated)

        # 4) antelopev2 face encoder — SHARED with the InstantID -FaceId block (5h-bis): the balazik README target is
        #    EXACTLY this same models\insightface\models\antelopev2 path. So GUARD on the SAME glintr100.onnx sentinel
        #    the InstantID block uses — if -FaceId already populated it, SKIP the download (no re-download / shadow
        #    across the two paths). If only -Pulid runs, fetch the SAME MonsterMMORPG mirror zip InstantID uses.
        $insDir = Join-Path $cmodels "insightface\models\antelopev2"; New-Item -ItemType Directory -Force $insDir | Out-Null
        $anteZip = Join-Path $insDir "antelopev2.zip"
        $anteOk  = Join-Path $insDir "glintr100.onnx"   # sentinel: shared with the InstantID block — present => skip
        if (-not (Test-Path $anteOk)) {
            Get-Model "https://huggingface.co/MonsterMMORPG/tools/resolve/main/antelopev2.zip" $anteZip
            if (Test-Path $anteZip) {
                Info "unzipping antelopev2 face encoder ..."
                try { Expand-Archive -Path $anteZip -DestinationPath $insDir -Force; Remove-Item $anteZip -Force -ErrorAction SilentlyContinue; Ok "antelopev2 unzipped -> $insDir" }
                catch { Warn "antelopev2 unzip failed ($($_.Exception.Message)); the PuLID-Flux node can auto-download it on first run instead" }
            } else { Warn "antelopev2 mirror unreachable; the PuLID-Flux node can auto-download it on first run instead" }
        } else { Ok "antelopev2 present (shared with InstantID -FaceId — not re-downloaded)" }
    } else { Warn "ComfyUI backend not found yet; re-run setup.ps1 -Media -Pulid after it installs" }

    # 5) workflow registration — GATED on the JSON existing. No authoritative SwarmUI-API PuLID.json is sourceable
    #    (balazik's examples/ are UI-graphs, not API-prompt) — it is the on-GPU authoring step (see the header
    #    comment + docs/decisions.md). Until media-assets\PuLID.json is authored + validated on a live GPU (which
    #    must FIRST verify the Alpha/stale node even LOADS on the current ComfyUI), copy nothing and Warn (same
    #    Test-Path posture as Foley/InstantID/InfiniteTalk). Once committed it rides the existing
    #    doki gen -Pulid -InitImage <face.png>  hook (comfyuicustomworkflow=PuLID), no C#/recipe change.
    $puWf  = Join-Path $root "media-assets\PuLID.json"
    $puCwf = Join-Path $swarm "src\BuiltinExtensions\ComfyUIBackend\CustomWorkflows\PuLID.json"
    if (Test-Path $puWf) { New-Item -ItemType Directory -Force (Split-Path $puCwf) | Out-Null; Copy-Item $puWf $puCwf -Force; Ok "PuLID workflow installed" }
    else { Warn "media-assets\PuLID.json not present (the runnable SwarmUI workflow is the on-GPU authoring step — see docs/decisions.md); node + weights installed, workflow skipped" }

    Ok "PuLID-Flux ready -> node + weights installed (~17GB non-gated FLUX fp8 base + pulid_flux v0.9.1; antelopev2 shared with -FaceId). On-GPU LABELED: FIRST verify the Alpha/stale balazik node LOADS on the current ComfyUI, THEN author the workflow JSON + confirm render quality. Run via  doki gen -Pulid -InitImage <face.png> '<prompt>'  once authored."
}

# 5h-quater. Nunchaku NVFP4 speed runtime (GATED sidecar, -Nunchaku) — mirrors the Foley/InstantID/InfiniteTalk
#     node+wheel pattern. Nunchaku is NOT a model: it is a SPEED RUNTIME (the nunchaku-ai/nunchaku pip wheel + the
#     ComfyUI-nunchaku node) that runs NVFP4-quantized model VARIANTS ~3x faster than BF16 on Blackwell/RTX-50xx.
#     (Org note: the wheel/node org is nunchaku-ai — the original mit-han-lab/nunchaku name is stale/redirects.)
#     Its value depends on whether a nunchaku NVFP4 variant exists for a model DokiDex runs. nunchaku DOES ship
#     two relevant ones here: (1) Z-Image-TURBO (nunchaku-ai/nunchaku-z-image-turbo svdq-fp4 — added nunchaku
#     v1.1.0, perf-boosted v1.2.0) — the HIGHEST-value add, since Z-Image-Turbo is DokiDex's #1 photoreal default
#     + real-time-canvas base, so this accelerates the MAIN draft path on Blackwell; (2) Qwen-Image
#     (nunchaku-ai/nunchaku-qwen-image svdq-fp4, the in-image-text model). FLUX.2 Klein NVFP4 is deliberately NOT
#     here: it is BFL's OWN NATIVE FP4 checkpoint (native ComfyUI/Diffusers FP4 path, NO svdq- prefix, NO nunchaku
#     dependency — nunchaku's changelog has zero FLUX.2 entries), so it ships under -Models full alongside the
#     plain Klein checkpoints, not in this gated block (see docs/decisions.md). The famous Nunchaku "4.4s" number
#     is 4090/int4 and does NOT transfer. ORDER MATTERS: the wheel must be in the comfy-python env BEFORE the node
#     loads (the node imports nunchaku at load), so wheel FIRST, then node + its requirements, then (under -Models
#     full) the NVFP4 weights. ON-GPU LABELED confirms (no GPU in CI): (1) which torch + CUDA build the live comfy
#     env has -> which wheel (PROBED below, not hardcoded — nunchaku is a compiled C++/CUDA ext with no fallback,
#     so torchX.Y AND cuXX.X MUST match exactly); (2) whether SwarmUI loads a single-file nunchaku .safetensors via
#     its normal -Model picker or needs the node's own Nunchaku loader / a custom workflow (if so, the svdq
#     Qwen/Z-Image NVFP4 weights become a -Workflow hook, not a bare -Model swap); (3) the real Blackwell speedup on 32GB.
if ($Nunchaku) {
    Info "Nunchaku NVFP4 speed runtime (nunchaku-ai wheel + ComfyUI-nunchaku node; Blackwell/RTX-50xx) — speeds up Z-Image-Turbo + Qwen-Image NVFP4 (Klein NVFP4 is BFL-native FP4, shipped under -Models full)"
    Ensure-WinGet "Git.Git" "git"
    $nodes = Join-Path $swarm "dlbackend\comfy\ComfyUI\custom_nodes"
    if (Test-Path $nodes) {
        # resolve the comfy-python (same 3-candidate probe Foley/InstantID/InfiniteTalk use).
        $cpy = @("dlbackend\comfy\python_embeded\python.exe", "dlbackend\comfy\venv\Scripts\python.exe", "dlbackend\comfy\ComfyUI\venv\Scripts\python.exe") |
            ForEach-Object { Join-Path $swarm $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1

        # 1) the WHEEL FIRST — pip-install the nunchaku C++/CUDA extension into the comfy env. The wheel MUST match
        #    the env's python version, its installed torch minor, AND its CUDA build EXACTLY (compiled ext, no
        #    fallback), so PROBE all three rather than hardcode. v1.2.1 ships TWO win_amd64 CUDA matrices:
        #    cu12.8 (torch 2.8/2.9/2.10/2.11) and cu13.0 (torch 2.9/2.10/2.11), each x cp310/311/312/313 — so a
        #    cu13 torch env needs the cu13.0 wheel or it import-fails. We read the live torch minor (e.g. '2.8'),
        #    torch.version.cuda (-> cu12.8 vs cu13.0), and the cpXYZ tag, then assemble the release-asset URL
        #    ('+' -> %2B). torch < 2.8 has NO matching wheel at all -> Warn (don't emit a guaranteed-404 URL).
        if ($cpy) {
            $tv = $null; $pyTag = $null; $cudaV = $null
            try { $tv    = (& $cpy -c "import torch,re;print(re.match(r'(\d+\.\d+)', torch.__version__).group(1))").Trim() } catch {}
            try { $pyTag = (& $cpy -c "import sys;print('cp%d%d' % sys.version_info[:2])").Trim() } catch {}
            try { $cudaV = (& $cpy -c "import torch;print(torch.version.cuda or '')").Trim() } catch {}
            # map torch.version.cuda -> the wheel's CUDA tag. '12.8'->cu12.8 ; '13.0' (or any 13.x)->cu13.0.
            # Unknown/older CUDA falls back to cu12.8 (the broadest matrix) with a heads-up rather than guessing.
            $cuTag = if ($cudaV -like '13.*') { 'cu13.0' } elseif ($cudaV -like '12.*') { 'cu12.8' } else { $null }
            # torch<2.8 ships no nunchaku wheel in any CUDA matrix; cu13.0 additionally has no torch2.8 wheel.
            $torchOk = $false
            if ($tv -match '^(\d+)\.(\d+)$') { $torchOk = ([int]$Matches[1] -gt 2) -or ([int]$Matches[1] -eq 2 -and [int]$Matches[2] -ge 8) }
            if (-not $torchOk -and $tv) {
                Warn "comfy env has torch $tv, but nunchaku requires torch>=2.8 (v1.2.1 ships only torch 2.8-2.11 wheels). Upgrade torch in the comfy env, then re-run  setup.ps1 -Media -Nunchaku  (the NVFP4 runtime needs a torch>=2.8 build to install)."
            } elseif ($tv -and $pyTag -and $cuTag) {
                if (-not $cudaV) { Warn "could not read torch.version.cuda; defaulting the nunchaku wheel to $cuTag — if torch is a cu13 build, install the cu13.0 wheel from https://github.com/nunchaku-tech/nunchaku/releases/tag/v1.2.1 manually." }
                $nver = "1.2.1"
                $asset = "nunchaku-$nver+${cuTag}torch$tv-$pyTag-$pyTag-win_amd64.whl"
                # '+' in the asset name -> %2B for the URL (PEP 427 build-tag; GitHub serves it URL-encoded).
                $wheelUrl = "https://github.com/nunchaku-tech/nunchaku/releases/download/v$nver/nunchaku-$nver%2B${cuTag}torch$tv-$pyTag-$pyTag-win_amd64.whl"
                Info "installing nunchaku wheel (torch $tv / CUDA $cudaV -> $cuTag / $pyTag): $asset ..."
                try { Pip $cpy install $wheelUrl; Ok "nunchaku wheel installed ($asset)" }
                catch { Warn "nunchaku wheel install failed for torch $tv / $cuTag / $pyTag ($($_.Exception.Message)). Pick the matching asset from https://github.com/nunchaku-tech/nunchaku/releases/tag/v$nver and  <comfy-python> -m pip install <url>  manually (or use the node's NunchakuWheelInstaller). NOTE cu13.0 has no torch2.8 wheel — upgrade torch if on cu13 + torch2.8." }
            } else {
                Warn "could not probe the comfy env's torch/python/CUDA (torch='$tv' py='$pyTag' cuda='$cudaV'); install the matching wheel from https://github.com/nunchaku-tech/nunchaku/releases/tag/v1.2.1 manually (the torchX.Y AND cuXX.X in the wheel name MUST match the env's torch minor + CUDA build exactly)."
            }
        } else {
            Warn "comfy-python not found; install the nunchaku wheel + node deps manually once the ComfyUI backend exists (re-run setup.ps1 -Media -Nunchaku after it installs)."
        }

        # 2) the NODE — clone ComfyUI-nunchaku (the loader/runtime node) + its requirements. Cloned AFTER the wheel
        #    so its load-time  import nunchaku  resolves. nunchaku-tech was GitHub-renamed to nunchaku-ai (both 200).
        $nNode = Join-Path $nodes "ComfyUI-nunchaku"
        if (-not (Test-Path $nNode)) { Info "installing ComfyUI-nunchaku node ..."; Git-Clone https://github.com/nunchaku-tech/ComfyUI-nunchaku $nNode } else { Ok "ComfyUI-nunchaku node present" }
        $nReq = Join-Path $nNode "requirements.txt"
        if ($cpy -and (Test-Path $nReq)) { Info "installing ComfyUI-nunchaku python deps ..."; try { Pip $cpy install -r $nReq; Ok "ComfyUI-nunchaku deps installed" } catch { Warn "ComfyUI-nunchaku deps: run  <comfy-python> -m pip install -r `"$nReq`"  manually ($($_.Exception.Message))" } }
        elseif ($cpy) { Ok "ComfyUI-nunchaku has no requirements.txt (deps satisfied by the wheel)" }
        else { Warn "ComfyUI-nunchaku deps: run  <comfy-python> -m pip install -r `"$nReq`"  manually" }
    } else { Warn "ComfyUI backend not found yet; re-run setup.ps1 -Media -Nunchaku after it installs" }

    # 3) the NVFP4 WEIGHTS — net-new nunchaku svdq weights, so gated on -Models full (like the Qwen GGUF). They go
    #    in the same diffusion_models dir ($diff) as the z_image/klein checkpoints; ComfyUI-nunchaku loads them via
    #    its own loader (the single-file-vs-loader-node routing is the on-GPU confirm in the header). Wired so a
    #    future NVFP4 model is one Get-Model away even when -Models is lean (node+wheel are tier-independent above).
    #    NOTE: FLUX.2 Klein NVFP4 is NOT here — it is BFL's OWN native FP4 (no nunchaku dep), so it ships under
    #    -Models full next to the plain Klein checkpoints (5g). Only the two nunchaku svdq weights belong in -Nunchaku:
    #      - Z-Image-Turbo NVFP4 (~3.91GB): nunchaku-ai/nunchaku-z-image-turbo svdq-fp4_r128 (HF-tree/HEAD verified;
    #        4-bit Z-Image-Turbo landed nunchaku v1.1.0). Accelerates DokiDex's DEFAULT photoreal + real-time-canvas
    #        base on Blackwell. The additive 'svdq-*z-image-turbo' branch in doki-gen.ps1 gives it the Turbo band
    #        (steps 8/cfg 1/euler/simple), matching the -Fast Z-Image-Turbo recipe.
    #      - Qwen-Image NVFP4 base (13.1GB): the svdq-fp4_r128 NON-Lightning base (matches the base t2i unet DokiDex
    #        already ships as the GGUF). The additive svdq-* line in doki-gen.ps1 gives it steps 20/cfg 4/euler/simple.
    if ($Models -eq "full") {
        Get-Model "https://huggingface.co/nunchaku-ai/nunchaku-z-image-turbo/resolve/main/svdq-fp4_r128-z-image-turbo.safetensors" (Join-Path $diff "svdq-fp4_r128-z-image-turbo.safetensors")
        Get-Model "https://huggingface.co/nunchaku-ai/nunchaku-qwen-image/resolve/main/svdq-fp4_r128-qwen-image.safetensors"        (Join-Path $diff "svdq-fp4_r128-qwen-image.safetensors")
    } else { Info "nunchaku NVFP4 weights skipped (re-run with -Models full to fetch the Z-Image-Turbo + Qwen-Image NVFP4 variants); node + wheel installed so any NVFP4 model is one Get-Model away" }

    Ok "Nunchaku ready -> NVFP4 runtime (wheel + node) installed$(if ($Models -eq 'full') { ' + Z-Image-Turbo/Qwen NVFP4 weights' }). Speeds up Z-Image-Turbo (the default base) + Qwen-Image on Blackwell (Klein NVFP4 is BFL-native FP4, under -Models full). ON-GPU LABELED: confirm the svdq loader is a plain -Model swap vs a custom workflow, the wheel/torch/CUDA match, and the real 32GB speedup."
}

# 5h-quinquies. TTS-Audio-Suite (GATED sidecar, -TtsSuite) — the 15-engine TTS + RVC ComfyUI custom node
#     (github.com/diodiogod/TTS-Audio-Suite, code license MIT). Mirrors the Foley/InstantID/InfiniteTalk node
#     install (Git-Clone + the 3-candidate comfy-python pip with the $cpy/else Warn graceful-degradation
#     fallback). NO weights are pre-fetched — all 15 engines auto-download their own (complete, current) model
#     sets on first node-use, so -TtsSuite ships the node + its deps and lets each engine fetch lazily.
#
#   ARCHITECTURE — this is a GATED ALTERNATIVE, NOT a replacement for the :8004 speech path. DokiDex's primary
#   TTS is the STANDALONE Chatterbox server (devnen/Chatterbox-TTS-Server) on :8004, installed by -Tts above
#   (control/Web/Tts.cs: Base=http://127.0.0.1:8004, POST /v1/audio/speech; the /api/speak endpoint Chat P4
#   voice-readback reuses). It lives in the LLM/llama-swap group and COEXISTS WITH CHAT — you can hear voice
#   readback while the coder model is loaded, with NO GPU-exclusivity. THAT path is the unconditional default
#   and is left BYTE-FOR-BYTE UNTOUCHED. TTS-Audio-Suite is a ComfyUI node that runs INSIDE SwarmUI's media
#   group, which is GPU-EXCLUSIVE with the LLM on 32GB (decisions.md 2026-06-13). So routing speech through it
#   means evicting the LLM to media mode just to speak — strictly worse for the chat-with-voice use case. Its
#   value is the EXTRA engines/features (IndexTTS-2 duration/emotion, Higgs v3's 100+ langs, RVC voice-changer)
#   for an explicit, opt-in `doki up media` -> `doki gen -Speak` flow, NOT the everyday readback path.
#
#   LICENSE CAVEATS (printed in the header below; verify before any claim — accurate, not the brief's assumed labels):
#     - Higgs Audio v3 (bosonai/higgs-audio-v3-tts-4b) = Boson "Higgs Audio v3 Research & Non-Commercial
#       License": research/non-commercial ONLY; hosted/revenue use needs a separate commercial license; bars
#       non-consensual voice cloning/impersonation. FINE for single-user/local. The single safetensors is 9.31GB.
#     - IndexTTS-2 (IndexTeam/IndexTTS-2) = bilibili "INDEX model license" — NOT a flat non-commercial license:
#       it PERMITS commercial use for individuals/small orgs; a separate bilibili license is needed ONLY above
#       100M MAU OR RMB 1B annual revenue, and it bars using the model to improve OTHER AI models. Fully fine
#       for DokiDex single-user (LESS restrictive than Higgs v3). Whole-dir ~5.9GB.
#     - Echo-TTS (one of the 15 auto-download engines) = CC-BY-NC-SA (non-commercial). RVC + most others are
#       permissive/MIT-compatible. ALL 15 engines AUTO-DOWNLOAD their own weights on first node use — setup
#       pre-fetches NOTHING (an earlier opt-in IndexTTS-2/Higgs pre-fetch was removed: it half-pinned Higgs to a
#       lone safetensors and used a single-file gpt.pth idempotency gate that couldn't detect a partial pull).
#
#   WORKFLOW IS NOT SHIPPED: the suite's example_workflows (the "🌈 IndexTTS-2 integration.json" etc.) are ComfyUI
#   UI-GRAPH exports (top-level id/nodes/links/groups/config), NOT SwarmUI's flat API-prompt CustomWorkflows
#   format — VERIFIED by fetching the raw IndexTTS-2 integration JSON. This is the IDENTICAL blocker InstantID /
#   InfiniteTalk hit. So the runnable per-engine media-assets\TtsSuite-*.json (e.g. TtsSuite-IndexTTS2.json /
#   TtsSuite-Higgs.json) are the ON-GPU authoring step (load the UI-graph live -> convert to API-prompt -> rewire
#   the text/voice/output injection points -> validate a render). Until they exist this installs the node + deps
#   only (engines auto-download their weights on first use) and Warns (same Test-Path posture as Foley/InstantID/
#   InfiniteTalk). It rides the existing  doki gen -Speak
#   -Engine <name>  hook (comfyuicustomworkflow=TtsSuite-<engine>), no C#/Tts.cs/api-speak change.
#   Cite: github.com/diodiogod/TTS-Audio-Suite/tree/main/example_workflows (UI-graph, not SwarmUI API-prompt).
#   ON-GPU LABELED confirms (no GPU in CI): (1) the engines AUTO-DOWNLOAD their own weights on first node-use (per
#   the suite README) into the node's models\TTS\<engine> convention — setup pre-fetches NOTHING (no half-pinned /
#   partial-pull risk); (2) whether SwarmUI's image/video-centric CustomWorkflow runner can host a pure-TTS node returning a WAV at all
#   (it may need an audio-output-node mapping that doesn't exist out of the box) — settle BEFORE promising the
#   -Speak route end-to-end; (3) the per-engine audio-input/text injection node names (the workflow authoring step).
if ($TtsSuite) {
    Info "TTS-Audio-Suite (diodiogod/TTS-Audio-Suite, MIT) — 15 TTS engines + RVC ComfyUI node. GATED ALTERNATIVE to the :8004 Chatterbox default (which stays the coexisting-with-chat speech path, UNTOUCHED); this runs in the GPU-exclusive media group."
    Warn "LICENSE: IndexTTS-2 = bilibili INDEX license (commercial OK for individuals/small orgs; separate license only >100M MAU or RMB 1B/yr; no using it to improve other models). Higgs Audio v3 = Boson Research/Non-Commercial (single-user/local OK; no non-consensual voice cloning). Echo-TTS = CC-BY-NC-SA. RVC/most others permissive. Local single-user use is fine."
    Ensure-WinGet "Git.Git" "git"
    $nodes = Join-Path $swarm "dlbackend\comfy\ComfyUI\custom_nodes"
    if (Test-Path $nodes) {
        # 1) the node
        $tsNode = Join-Path $nodes "TTS-Audio-Suite"
        if (-not (Test-Path $tsNode)) { Info "installing TTS-Audio-Suite node ..."; Git-Clone https://github.com/diodiogod/TTS-Audio-Suite $tsNode } else { Ok "TTS-Audio-Suite node present" }
        # 2) its python deps (same 3-candidate comfy-python probe Foley/InstantID/InfiniteTalk use, with the
        #    $cpy/else Warn graceful-degradation fallback). requirements.txt covers the 15 engines' shared deps.
        $cpy = @("dlbackend\comfy\python_embeded\python.exe", "dlbackend\comfy\venv\Scripts\python.exe", "dlbackend\comfy\ComfyUI\venv\Scripts\python.exe") |
            ForEach-Object { Join-Path $swarm $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        $tsReq = Join-Path $tsNode "requirements.txt"
        if ($cpy -and (Test-Path $tsReq)) { Info "installing TTS-Audio-Suite python deps ..."; try { Pip $cpy install -r $tsReq; Ok "TTS-Audio-Suite deps installed" } catch { Warn "TTS-Audio-Suite deps: run  <comfy-python> -m pip install -r `"$tsReq`"  manually ($($_.Exception.Message))" } }
        else { Warn "TTS-Audio-Suite deps: run  <comfy-python> -m pip install -r `"$tsReq`"  manually" }

        # 3) WEIGHTS — NOT pre-fetched. The diodiogod/TTS-Audio-Suite README states ALL 15 engines AUTO-DOWNLOAD
        #    their own models on first node-use (into the suite's models\TTS\<engine> convention), so -TtsSuite
        #    deliberately ships the node + its pip deps and lets each engine fetch its weights lazily on the first
        #    `doki gen -Speak -Engine <engine>` that actually runs it. This REPLACES an earlier opt-in pre-fetch of
        #    IndexTTS-2 (via `hf`) and Higgs v3 (a lone model.safetensors via Get-Model), which carried two footguns:
        #    (a) Higgs was HALF-PINNED — only model.safetensors was fetched, but the HF repo also ships
        #    model.safetensors.index.json + config.json + tokenizer files; a sharded-aware loader that sees the
        #    index.json may refuse to load a lone weight; (b) the IndexTTS-2 idempotency gate keyed only on
        #    Test-Path gpt.pth, so an interrupted multi-file pull (gpt.pth landed, s2mel.pth / the qwen subfolder
        #    did not) was treated as complete and never resumed. Auto-download-on-first-use eliminates BOTH (the node
        #    always pulls the engine's COMPLETE, current file set), so there is no half-pinned/partial-pull risk and
        #    no -Models-full tier gate to reason about here. See docs/decisions.md (the TTS-Audio-Suite note).
    } else { Warn "ComfyUI backend not found yet; re-run setup.ps1 -Media -TtsSuite after it installs" }

    # 4) workflow registration — GATED on the per-engine JSON existing. No authoritative SwarmUI-API TtsSuite-*.json
    #    is sourceable (the suite's example_workflows are UI-graphs, not API-prompt — see the header) — it is the
    #    on-GPU authoring step (one JSON per engine, e.g. TtsSuite-IndexTTS2.json / TtsSuite-Higgs.json). Until they
    #    exist, copy whatever IS authored and Warn for the rest (same Test-Path posture as Foley/InstantID/InfiniteTalk).
    #    Once committed, each rides the  doki gen -Speak -Engine <engine>  hook (comfyuicustomworkflow=TtsSuite-<engine>).
    $tsWfDir  = Join-Path $root "media-assets"
    $tsCwfDir = Join-Path $swarm "src\BuiltinExtensions\ComfyUIBackend\CustomWorkflows"
    $tsWfs = @(Get-ChildItem -Path $tsWfDir -Filter "TtsSuite-*.json" -ErrorAction SilentlyContinue)
    if ($tsWfs.Count -gt 0) {
        New-Item -ItemType Directory -Force $tsCwfDir | Out-Null
        foreach ($w in $tsWfs) { Copy-Item $w.FullName (Join-Path $tsCwfDir $w.Name) -Force; Ok "TtsSuite workflow installed: $($w.Name)" }
    } else { Warn "media-assets\TtsSuite-*.json not present (the runnable per-engine SwarmUI workflows are the on-GPU authoring step — see docs/decisions.md); node + deps installed (engines auto-download their weights on first use), workflows skipped" }

    Ok "TTS-Audio-Suite ready -> node + deps installed (all 15 engines AUTO-DOWNLOAD their weights on first -Speak use — nothing pre-fetched, so no half-pinned/partial-pull risk). The :8004 Chatterbox path is UNCHANGED (still the coexisting-with-chat default). ON-GPU LABELED: author the per-engine workflow JSON, confirm SwarmUI can host a WAV-output TTS node, and pin the auto-download paths. Run via  doki up media  then  doki gen -Speak -Engine IndexTTS2 '<text>' [-Audio ref.wav]  once authored."
}

# 5i. Configure MagicPrompt -> local prompt-rewriter (:8013) HEADLESSLY via its API
#     (backend 'OpenAI API (Local)', base URL :8013 with no /v1, model 'prompt-rewriter').
$mpInstr = "You are a cinematographer rewriting a user's short prompt into ONE rich, vivid video-generation prompt. Keep the user's subject and action exactly; never censor, swap, refuse, or moralize. Output ENGLISH only, 60-120 words, no preamble. Add up to 4 cinematic details that fit (shot size, lighting, light direction, color tone, camera angle, and ONE camera move unless motion is already specified). Describe the motion concretely and add subtle ambient motion. Flesh out subject and background detail without inventing new subjects. If a style is named put it first; if non-photoreal, skip photographic lighting terms. Avoid abstract mood words. Default to daytime and center composition when unspecified."
try {
    $mpSid  = (Invoke-RestMethod "http://127.0.0.1:7801/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
    $mpBody = @{ session_id = $mpSid; settings = @{ backend = "openaiapi"; model = "prompt-rewriter"; baseurl = "http://127.0.0.1:8013"; instructions = @{ prompt = $mpInstr } } } | ConvertTo-Json -Depth 8
    $mpResp = Invoke-RestMethod "http://127.0.0.1:7801/API/SaveMagicPromptSettings" -Method Post -Body $mpBody -ContentType 'application/json'
    if ($mpResp.success) { Ok "MagicPrompt configured -> prompt-rewriter on :8013  (use  <mpprompt:your idea>  in any prompt)" }
    else { Warn "MagicPrompt auto-config returned no success; set it in the MagicPrompt tab (URL http://127.0.0.1:8013, model prompt-rewriter)" }
} catch { Warn "MagicPrompt auto-config failed ($($_.Exception.Message)); set it in the MagicPrompt tab (backend 'OpenAI API (Local)', URL http://127.0.0.1:8013, model prompt-rewriter)" }

Info "media stack ready."
Ok "image + video generation at http://127.0.0.1:7801   (manage via: .\doki.ps1 up media)"
