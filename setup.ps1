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

if (-not $Media) { Info "core setup done. Re-run with -Media to add image + video generation."; return }

# ---- 5. Media stack: SwarmUI + ComfyUI + uncensored models ----------------
Info "media stack (SwarmUI + ComfyUI + uncensored image/video models)"

# 5a. prereqs: .NET 8 SDK + git
if (-not ((dotnet --list-sdks 2>$null) -match '8\.0\.')) { Ensure-WinGet "Microsoft.DotNet.SDK.8" $null } else { Ok ".NET 8 SDK present" }
Ensure-WinGet "Git.Git" "git"

# 5b. clone + 5c. build SwarmUI
$swarm = Join-Path $root "media\SwarmUI"
if (-not (Test-Path (Join-Path $swarm ".git"))) { Info "cloning SwarmUI ..."; git clone https://github.com/mcmonkeyprojects/SwarmUI $swarm } else { Ok "SwarmUI cloned" }
$swarmExe = Join-Path $swarm "src\bin\live_release\SwarmUI.exe"
if (-not (Test-Path $swarmExe)) {
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
function Resolve-HF($repo, $sub, $pattern) {
    try { (Invoke-RestMethod "https://huggingface.co/api/models/$repo/tree/main$sub" | Where-Object { $_.path -match $pattern -and $_.path -match '\.safetensors$' } | Select-Object -First 1).path } catch { $null }
}
# --- lean: the verified reliable defaults ---
# image: Z-Image Turbo (uncensored, fast, photoreal) — verified ~seconds/image
Get-Model "https://huggingface.co/mcmonkey/swarm-models/resolve/main/SwarmUI_Z-Image-Turbo-FP8Mix.safetensors" (Join-Path $diff "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors")
# video: Wan 2.1 1.3B (uncensored) — fits 32GB with headroom, ~25s/clip. The reliable default.
Get-Model "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files/diffusion_models/wan2.1_t2v_1.3B_fp16.safetensors" (Join-Path $diff "wan2.1_t2v_1.3B_fp16.safetensors")

# --- full: higher quality, heavier (Wan-14B is minutes/clip & VRAM-tight on 32GB) ---
if ($Models -eq "full") {
    $wanRepo = "Comfy-Org/Wan_2.1_ComfyUI_repackaged"
    $wanFile = Resolve-HF $wanRepo "/split_files/diffusion_models" '(?i)t2v.*14B.*fp8'
    if ($wanFile) { Get-Model "https://huggingface.co/$wanRepo/resolve/main/$wanFile" (Join-Path $diff (Split-Path $wanFile -Leaf)) }
    Get-Model "https://huggingface.co/Kijai/WanVideo_comfy/resolve/main/Wan21_T2V_14B_lightx2v_cfg_step_distill_lora_rank32.safetensors" (Join-Path $loraD "Wan21_T2V_14B_lightx2v_rank32.safetensors")
    # Chroma — uncensored, FLUX-derived. Use the *-final STABLE variant; the repo also
    # has do_not_use/ experimental files that error in ComfyUI (tensor mismatch).
    Get-Model "https://huggingface.co/silveroxides/Chroma1-HD-fp8-scaled/resolve/main/Chroma1-HD-fp8mixed-final.safetensors" (Join-Path $diff "Chroma1-HD-fp8mixed-final.safetensors")
    $ltx = Resolve-HF "Lightricks/LTX-Video" "" '(?i)ltx-video.*0\.9\.[56]'
    if ($ltx) { Get-Model "https://huggingface.co/Lightricks/LTX-Video/resolve/main/$ltx" (Join-Path (Join-Path $swarm "Models\Stable-Diffusion") (Split-Path $ltx -Leaf)) }
}

# 5g. refresh model list
try {
    $sid2 = (Invoke-RestMethod "http://127.0.0.1:7801/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
    Invoke-RestMethod "http://127.0.0.1:7801/API/TriggerRefresh" -Method Post -Body (@{ session_id = $sid2 } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
} catch {}
Info "media stack ready."
Ok "image + video generation at http://127.0.0.1:7801   (manage via: .\doki.ps1 up media)"
