# setup.ps1 — one-command DokiCode bootstrap (native, no Docker). Idempotent.
#
# Verifies prereqs, deploys configs, and with -Media installs the fully-local,
# uncensored image + video generation stack (SwarmUI + ComfyUI + models),
# completely headlessly (no GUI install wizard).
#
# Usage:
#   .\setup.ps1                       core: prereqs + config deploy (LLM/chat/code)
#   .\setup.ps1 -Media                + SwarmUI/ComfyUI + the verified uncensored models
#   .\setup.ps1 -Media -Models full   + extras (Chroma image, LTX-Video)
#
# Then:  .\doki.ps1 up        (chat + code)        .\doki.ps1 up media   (image + video)
param(
    [switch]$Media,
    [switch]$Tts,
    [ValidateSet("lean", "full")][string]$Models = "lean"
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$root = $PSScriptRoot

function Info($m) { Write-Host "[setup] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  ok  $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  !!  $m" -ForegroundColor Yellow }
function Ensure-WinGet($id, $cmd) {
    if ($cmd -and (Get-Command $cmd -ErrorAction SilentlyContinue)) { Ok "$cmd present"; return }
    Info "installing $id ..."
    winget install $id --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
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
Copy-Item (Join-Path $root "harness\crush.json") (Join-Path $crushDst "crush.json") -Force
Ok "crush.json -> $crushDst"
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

# ---- TTS stack: uncensored speech + zero-shot voice cloning (Chatterbox) — optional, works with or without -Media ----
if ($Tts) {
    Info "TTS stack (Chatterbox: uncensored speech + zero-shot voice cloning)"
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Ensure-WinGet "Python.Python.3.10" "python" }
    Ensure-WinGet "Git.Git" "git"
    $ttsRoot = Join-Path $root "tts\Chatterbox-TTS-Server"
    if (-not (Test-Path (Join-Path $ttsRoot ".git"))) { Info "cloning Chatterbox-TTS-Server ..."; git clone https://github.com/devnen/Chatterbox-TTS-Server $ttsRoot } else { Ok "Chatterbox-TTS-Server cloned" }
    $tpy = Join-Path $ttsRoot ".venv\Scripts\python.exe"
    if (-not (Test-Path $tpy)) {
        Info "creating venv + installing cu128 torch + deps (large, ~3GB) ..."
        python -m venv (Join-Path $ttsRoot ".venv")
        & $tpy -m pip install --upgrade pip | Out-Null
        & $tpy -m pip install -r (Join-Path $ttsRoot "requirements-nvidia-cu128.txt")
        # chatterbox itself with --no-deps so it can't downgrade the cu128 torch
        & $tpy -m pip install --no-deps "git+https://github.com/devnen/chatterbox-v2.git@master" s3tokenizer==0.3.0 onnx==1.16.0
        # onnx needs protobuf >=3.20 but the cu128 reqs pin 3.19.6 (descript-audiotools) — fix it
        & $tpy -m pip install protobuf==4.25.5
    } else { Ok "TTS venv present" }
    # Use the public ORIGINAL model — the server's default 'chatterbox-turbo' repo is gated.
    $cfg = Join-Path $ttsRoot "config.yaml"
    if (Test-Path $cfg) { (Get-Content $cfg) -replace 'repo_id: chatterbox-turbo', 'repo_id: chatterbox' | Set-Content $cfg }
    # Strip the Perth watermark in every chatterbox model file (genuinely unmarked, uncensored output).
    $cbDir = Join-Path $ttsRoot ".venv\Lib\site-packages\chatterbox"
    foreach ($f in "tts.py", "mtl_tts.py", "tts_turbo.py", "vc.py") {
        $fp = Join-Path $cbDir $f
        if (Test-Path $fp) { (Get-Content $fp) -replace 'self\.watermarker\.apply_watermark\(wav, sample_rate=self\.sr\)', 'wav  # watermark stripped (DokiCode: uncensored)' | Set-Content $fp }
    }
    Ok "TTS ready -> :8004 (OpenAI /v1/audio/speech + voice cloning). First '.\doki.ps1 up' downloads the voice model."
}

if (-not $Media) { Info "core setup done.  -Media adds image/video,  -Tts adds speech."; return }

# ---- 5. Media stack: SwarmUI + ComfyUI + uncensored models ----------------
Info "media stack (SwarmUI + ComfyUI + uncensored image/video models)"

# 5a. prereqs: .NET 8 SDK + git
if (-not ((dotnet --list-sdks 2>$null) -match '8\.0\.')) { Ensure-WinGet "Microsoft.DotNet.SDK.8" $null } else { Ok ".NET 8 SDK present" }
Ensure-WinGet "Git.Git" "git"

# 5b. clone SwarmUI
$swarm = Join-Path $root "media\SwarmUI"
if (-not (Test-Path (Join-Path $swarm ".git"))) { Info "cloning SwarmUI ..."; git clone https://github.com/mcmonkeyprojects/SwarmUI $swarm } else { Ok "SwarmUI cloned" }

# 5b2. install the MagicPrompt extension (local-LLM prompt enhancement) BEFORE the build so it
#      compiles in. Adding it forces a rebuild even if SwarmUI was already built.
$mpExt = Join-Path $swarm "src\Extensions\SwarmUI-MagicPromptExtension"
$extAdded = $false
if (-not (Test-Path $mpExt)) { Info "installing MagicPrompt extension ..."; git clone https://github.com/HartsyAI/SwarmUI-MagicPromptExtension $mpExt; $extAdded = $true } else { Ok "MagicPrompt extension present" }

# 5c. build SwarmUI (also rebuild when a new extension was just added)
$swarmExe = Join-Path $swarm "src\bin\live_release\SwarmUI.exe"
if ((-not (Test-Path $swarmExe)) -or $extAdded) {
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

# 5e. headless ComfyUI backend install (the verified InstallConfirmWS flow)
if (-not (Test-Path (Join-Path $swarm "dlbackend\comfy"))) {
    Info "installing ComfyUI backend headlessly (~2GB download) ..."
    $sid = (Invoke-RestMethod "http://127.0.0.1:7801/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
    $ws = [System.Net.WebSockets.ClientWebSocket]::new(); $ct = [Threading.CancellationToken]::None
    $ws.ConnectAsync([Uri]"ws://127.0.0.1:7801/API/InstallConfirmWS", $ct).GetAwaiter().GetResult() | Out-Null
    $payload = @{ session_id = $sid; theme = "modern_dark"; installed_for = "just_self"; backend = "comfyui"; models = "none"; install_amd = $false; language = "en"; make_shortcut = $false } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $ws.SendAsync([System.ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).GetAwaiter().GetResult() | Out-Null
    $buf = New-Object byte[] 32768; $sb = New-Object System.Text.StringBuilder; $deadline = (Get-Date).AddMinutes(20)
    while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open -and (Get-Date) -lt $deadline) {
        $r = $ws.ReceiveAsync([System.ArraySegment[byte]]::new($buf), $ct).GetAwaiter().GetResult()
        if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { break }
        [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
        if ($r.EndOfMessage) {
            $f = $sb.ToString(); [void]$sb.Clear()
            if ($f -match '"info":"([^"]*)"') { Info $Matches[1] }
            if ($f -match '"success"\s*:\s*true') { break }
            if ($f -match '"error"') { throw "backend install error: $f" }
        }
    }
    $ws.Dispose()
    if (Test-Path (Join-Path $swarm "dlbackend\comfy")) { Ok "ComfyUI backend installed" } else { throw "backend install did not complete" }
} else { Ok "ComfyUI backend present" }

# 5f. download uncensored models (idempotent)
$diff = Join-Path $swarm "Models\diffusion_models"; New-Item -ItemType Directory -Force $diff | Out-Null
$loraD = Join-Path $swarm "Models\Lora"; New-Item -ItemType Directory -Force $loraD | Out-Null
function Get-Model($url, $dest) {
    if (Test-Path $dest) { Ok "have $(Split-Path $dest -Leaf)"; return }
    if (-not $url) { Warn "could not resolve $(Split-Path $dest -Leaf)"; return }
    Info "downloading $(Split-Path $dest -Leaf) ..."
    curl.exe -L --fail --retry 3 -o $dest $url
    if ($LASTEXITCODE -ne 0) { Warn "download failed: $url" } else { Ok "$(Split-Path $dest -Leaf) ($([math]::Round((Get-Item $dest).Length/1GB,2)) GB)" }
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

    # Video quality+fast tier: Wan 2.2 14B MoE (high+low noise pair, fp8_scaled). Only ONE
    # expert is GPU-resident per phase (SwarmUI StepSwap), so this fits 32GB with headroom.
    # NOTE: Wan 2.5/2.6/2.7 are API-only — Wan 2.2 is the newest OPEN-weight Wan that exists.
    $w22 = "https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged/resolve/main/split_files"
    Get-Model "$w22/diffusion_models/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors" (Join-Path $diff "wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors"  (Join-Path $diff "wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors" (Join-Path $diff "wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors"  (Join-Path $diff "wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors")
    Get-Model "$w22/diffusion_models/wan2.2_ti2v_5B_fp16.safetensors"                  (Join-Path $diff "wan2.2_ti2v_5B_fp16.safetensors")    # fast preview; no fp8 exists
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

    # Image quality ceiling: Z-Image Base (non-distilled). Reuses the qwen_3_4b text encoder
    # + Flux ae VAE that SwarmUI already auto-fetched for Z-Image Turbo. Turbo stays default.
    Get-Model "https://huggingface.co/Comfy-Org/z_image/resolve/main/split_files/diffusion_models/z_image_bf16.safetensors" (Join-Path $diff "z_image_bf16.safetensors")

    # Chroma — uncensored, FLUX-derived stylized complement. Use the *-final STABLE variant
    # (the repo's do_not_use/ files error in ComfyUI with a tensor mismatch).
    Get-Model "https://huggingface.co/silveroxides/Chroma1-HD-fp8-scaled/resolve/main/Chroma1-HD-fp8mixed-final.safetensors" (Join-Path $diff "Chroma1-HD-fp8mixed-final.safetensors")

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
        if (-not (Test-Path $foleyNode)) { Info "installing HunyuanVideo-Foley node ..."; git clone https://github.com/phazei/ComfyUI-HunyuanVideo-Foley $foleyNode } else { Ok "Foley node present" }
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
