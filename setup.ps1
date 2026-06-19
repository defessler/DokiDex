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
    [switch]$Vision,    # optional: vision model (Qwen3-VL-8B) -> lights up the studio Describe/Verify surfaces
    [switch]$LlmCandidates,  # optional: download the coder/heavy bake-off candidates (Qwen3.6 / Qwen3-Coder-Next) for eval
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
