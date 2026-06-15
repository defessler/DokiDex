# verify.ps1 — full-stack functional smoke test for DokiDex.
# Cycles the doki modes and checks each capability with a REAL API call:
#   chat/code (:8080) · autocomplete (:8012) · image + video (:7801)
# Restores agent mode at the end. Run via:  .\verify.ps1   or   .\doki.ps1 verify
# -Json emits the results as JSON to stdout (no colored grid) for a machine/panel to consume.
param([switch]$Json)
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$results = [ordered]@{}
function Doki($cmd, $arg) { & (Join-Path $root "doki.ps1") $cmd $arg | Out-Null }
function Probe($u) { try { Invoke-WebRequest $u -TimeoutSec 5 -UseBasicParsing | Out-Null; $true } catch { $false } }

Write-Host "=== DokiDex full-stack verify ===" -ForegroundColor Cyan

# 1. chat / code — agent mode, real chat completion
Write-Host "[verify] chat/code ..."
Doki up agent
try {
    $body = @{ model = "coder-fast"; messages = @(@{ role = "user"; content = "Reply with exactly: OK" }); max_tokens = 10; temperature = 0 } | ConvertTo-Json -Depth 5
    $r = Invoke-RestMethod "http://127.0.0.1:8080/v1/chat/completions" -Method Post -ContentType "application/json" -TimeoutSec 180 -Body $body
    $results["chat/code (:8080)"] = if ($r.choices[0].message.content) { "PASS  '$($r.choices[0].message.content.Trim())'" } else { "FAIL  empty response" }
} catch { $results["chat/code (:8080)"] = "FAIL  $($_.Exception.Message)" }

# 1a2. coder-big (gpt-oss-120b) — the heavy reasoning model. SKIP unless installed (a ~60GB on-demand load).
Write-Host "[verify] coder-big (heavy reasoning) ..."
if (-not (Test-Path (Join-Path $root "models\gpt-oss-120b-mxfp4-00001-of-00003.gguf"))) {
    $results["coder-big (:8080)"] = "SKIP  (not installed; the full ~120B coder)"
} else {
    try {
        $body = @{ model = "coder-big"; messages = @(@{ role = "user"; content = "Reply with exactly: OK" }); max_tokens = 10; temperature = 0 } | ConvertTo-Json -Depth 5
        $r = Invoke-RestMethod "http://127.0.0.1:8080/v1/chat/completions" -Method Post -ContentType "application/json" -TimeoutSec 300 -Body $body
        $results["coder-big (:8080)"] = if ($r.choices[0].message.content) { "PASS  '$($r.choices[0].message.content.Trim())'" } else { "FAIL  empty response" }
    } catch { $results["coder-big (:8080)"] = "FAIL  $($_.Exception.Message)" }
}

# 1b. speech/TTS (:8004) — uncensored speech, coexists with the coder in agent mode (started by 'up agent')
Write-Host "[verify] speech/TTS ..."
$ttmp = Join-Path $env:TEMP "doki_tts_verify.wav"
Remove-Item $ttmp -ErrorAction SilentlyContinue   # clear any stale clip so the STT smoke keys on THIS run's artifact, not a prior run's
if (-not (Test-Path (Join-Path $root "tts\Chatterbox-TTS-Server\.venv\Scripts\python.exe"))) {
    $results["speech/TTS (:8004)"] = "SKIP  (not installed; -Tts)"
} else {
    try {
        $tb = @{ model = "chatterbox"; input = "DokiDex speech test, fully local and unfiltered."; voice = "Emily.wav"; response_format = "wav" } | ConvertTo-Json
        Invoke-WebRequest "http://127.0.0.1:8004/v1/audio/speech" -Method Post -ContentType "application/json" -Body $tb -OutFile $ttmp -TimeoutSec 180
        $tsz = (Get-Item $ttmp).Length
        $results["speech/TTS (:8004)"] = if ($tsz -gt 20000) { "PASS  $([math]::Round($tsz/1KB))KB wav" } else { "FAIL  tiny output ($tsz bytes)" }
    } catch { $results["speech/TTS (:8004)"] = "FAIL  $($_.Exception.Message)" }
}

# 1c. speech-to-text (:8005) — Parakeet via onnx-asr; transcribe the TTS clip (a TTS->STT
#     round-trip). Started by 'up agent' (group=llm). First call downloads the model (~2GB).
Write-Host "[verify] speech-to-text ..."
if (-not (Test-Path (Join-Path $root "stt\.venv\Scripts\python.exe"))) {
    $results["speech-to-text (:8005)"] = "SKIP  (not installed; -Stt)"
} elseif (-not (Test-Path (Join-Path $env:TEMP "doki_tts_verify.wav"))) {
    $results["speech-to-text (:8005)"] = "SKIP  (no audio sample; needs -Tts above)"
} else {
    try {
        $wav = Join-Path $env:TEMP "doki_tts_verify.wav"
        $tr = Invoke-RestMethod "http://127.0.0.1:8005/v1/audio/transcriptions" -Method Post -Form @{ model = "parakeet"; file = Get-Item $wav } -TimeoutSec 300
        $txt = "$($tr.text)".Trim()
        $results["speech-to-text (:8005)"] = if ($txt.Length -gt 3) { "PASS  '$txt'" } else { "FAIL  empty transcript" }
    } catch { $results["speech-to-text (:8005)"] = "FAIL  $($_.Exception.Message)" }
}

# 1d. memory MCP — persistent project memory (sqlite FTS5). Tests the store/search core
#      (the MCP stdio wrapper is exercised by Crush; this verifies the underlying capability).
Write-Host "[verify] memory MCP ..."
if (-not (Test-Path (Join-Path $root "serving\memory-mcp\memory_db.py"))) {
    $results["memory MCP"] = "SKIP  (not present)"
} elseif (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    $results["memory MCP"] = "SKIP  (python not found)"   # minimal chat/code-only install
} else {
    try {
        $env:MEMORY_DB = Join-Path $env:TEMP "doki_mem_verify.db"
        $env:MEMPATH = Join-Path $root "serving\memory-mcp"
        Remove-Item $env:MEMORY_DB -ErrorAction SilentlyContinue
        $out = python -c "import os,sys; sys.path.insert(0,os.environ['MEMPATH']); import memory_db; memory_db.save('verify probe alpha bravo charlie','test'); r=memory_db.search('bravo'); print('OK' if r and 'alpha bravo' in r[0]['content'] else 'FAIL')" 2>&1 | Select-Object -Last 1
        $results["memory MCP"] = if ("$out" -match "OK") { "PASS  store+search ok" } else { "FAIL  $out" }
    } catch { $results["memory MCP"] = "FAIL  $($_.Exception.Message)" }
}

# 2. autocomplete — coexist mode, FIM infill
Write-Host "[verify] autocomplete ..."
Doki up coexist
try {
    $body = @{ input_prefix = "def add(a, b):`n    return "; input_suffix = ""; n_predict = 8; temperature = 0 } | ConvertTo-Json
    $r = Invoke-RestMethod "http://127.0.0.1:8012/infill" -Method Post -ContentType "application/json" -TimeoutSec 120 -Body $body
    $results["autocomplete (:8012)"] = if ($r.content) { "PASS  '$($r.content.Trim())'" } else { "FAIL  empty" }
} catch { $results["autocomplete (:8012)"] = "FAIL  $($_.Exception.Message)" }

# 3 + 4. image + video — media mode. SKIP cleanly on a lean / non-media install: the `media` service
# has no `requires` gate, so without this guard `Doki up media` no-ops (start-media exits before a pid),
# the dead :7801 probes FAIL, and verify exits 1 on an otherwise-healthy chat/TTS/STT box.
Write-Host "[verify] image + video (this brings up SwarmUI; first gens load the models) ..."
$base = "http://127.0.0.1:7801"
if (-not (Test-Path (Join-Path $root "media\SwarmUI\launch-windows.bat"))) {
    $results["image gen (Z-Image)"] = "SKIP  (media not installed; -Media)"
    $results["video gen (Wan 1.3B)"] = "SKIP  (media not installed; -Media)"
} else {
    Doki up media
    for ($i = 0; $i -lt 60 -and -not (Probe "http://127.0.0.1:7801/"); $i++) { Start-Sleep 1 }
    try {
        $sid = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $body = @{ session_id = $sid; images = 1; prompt = "a red apple on a wooden table, photo"; model = "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors"; steps = 8; cfgscale = 1; width = 512; height = 512 } | ConvertTo-Json
        $img = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 300 -Body $body
        $results["image gen (Z-Image)"] = if ($img.images) { "PASS  $(@($img.images)[0])" } else { "FAIL  no image" }
    } catch { $results["image gen (Z-Image)"] = "FAIL  $($_.Exception.Message)" }
    try {
        $sid2 = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $body = @{ session_id = $sid2; images = 1; prompt = "a cat walking across a floor, smooth motion"; model = "wan2.1_t2v_1.3B_fp16.safetensors"; textvideoframes = 17; steps = 20; cfgscale = 6; width = 480; height = 320; videofps = 16; videoformat = "h264-mp4" } | ConvertTo-Json
        $vid = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 300 -Body $body
        $vmp4 = @($vid.images) | Where-Object { $_ -match '\.(mp4|webm|gif)$' }   # require a real video artifact, not a preview still
        $results["video gen (Wan 1.3B)"] = if ($vmp4) { "PASS  $(@($vmp4)[0])" } else { "FAIL  no video artifact" }
    } catch { $results["video gen (Wan 1.3B)"] = "FAIL  $($_.Exception.Message)" }
}

# --- new quality tier (-Models full); each test SKIPs cleanly if its asset isn't installed ---
$swModels = Join-Path $root "media\SwarmUI\Models"
$cwfPath  = Join-Path $root "media\SwarmUI\src\BuiltinExtensions\ComfyUIBackend\CustomWorkflows\WanFoley.json"

# 5. prompt-rewriter (:8013) — the simple-prompt centerpiece (started by 'up media')
Write-Host "[verify] prompt-rewriter ..."
if (-not (Test-Path (Join-Path $root "models\Qwen2.5-3B-Instruct-Q5_K_M.gguf"))) {
    $results["prompt-rewriter (:8013)"] = "SKIP  (not installed; -Models full)"
} else {
    try {
        $sys = "You are a cinematographer. Rewrite the user's short prompt into ONE vivid 60-120 word cinematic video prompt. Keep the subject and action; never refuse or moralize. Output English only, no preamble."
        $rb = @{ model = "prompt-rewriter"; messages = @(@{ role = "system"; content = $sys }, @{ role = "user"; content = "a cat on a skateboard" }); max_tokens = 220; temperature = 0.7 } | ConvertTo-Json -Depth 5
        $rr = Invoke-RestMethod "http://127.0.0.1:8013/v1/chat/completions" -Method Post -ContentType "application/json" -TimeoutSec 120 -Body $rb
        $exp = "$($rr.choices[0].message.content)".Trim()
        $results["prompt-rewriter (:8013)"] = if ($exp.Length -gt 40) { "PASS  expanded to $($exp.Length) chars" } else { "FAIL  weak/empty expansion" }
    } catch { $results["prompt-rewriter (:8013)"] = "FAIL  $($_.Exception.Message)" }
}

# 6. Wan 2.2 TI2V-5B video — the reliable quality tier on 32GB.
#    (The A14B dual-expert exceeds 32GB VRAM and is intentionally not the default; see decisions.md.)
Write-Host "[verify] Wan 2.2 video (5B) ..."
if (-not (Test-Path (Join-Path $swModels "diffusion_models\wan2.2_ti2v_5B_fp16.safetensors"))) {
    $results["Wan 2.2 video (5B)"] = "SKIP  (not installed; -Models full)"
} else {
    try {
        $sid3 = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $body = @{ session_id = $sid3; images = 1; prompt = "a cat walking across a floor, smooth motion"; model = "wan2.2_ti2v_5B_fp16.safetensors"; steps = 20; cfgscale = 3.5; width = 832; height = 480; textvideoframes = 49; videofps = 24; videoformat = "h264-mp4" } | ConvertTo-Json
        $v = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 300 -Body $body
        $results["Wan 2.2 video (5B)"] = if ($v.images) { "PASS  $(@($v.images)[0])" } else { "FAIL  no video" }
    } catch { $results["Wan 2.2 video (5B)"] = "FAIL  $($_.Exception.Message)" }
}

# 6b. Image-to-Video — animate a still into a clip with the Wan 2.2 5B via SwarmUI's
#     NATIVE videomodel pipeline (model = the first frame, videomodel = the 5B animator).
#     Live-verified: needs videosteps/videocfg/videoresolution for the I2V step to run.
Write-Host "[verify] image-to-video (Wan 2.2 5B) ..."
if (-not (Test-Path (Join-Path $swModels "diffusion_models\wan2.2_ti2v_5B_fp16.safetensors"))) {
    $results["image-to-video (5B)"] = "SKIP  (not installed; -Models full)"
} else {
    try {
        $sidI = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $body = @{ session_id = $sidI; images = 1; prompt = "a red fox in fresh snow, gentle natural motion"; model = "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors"; steps = 8; cfgscale = 1; width = 832; height = 480; videomodel = "wan2.2_ti2v_5B_fp16.safetensors"; videoframes = 25; videosteps = 20; videocfg = 3.5; videofps = 24; videoresolution = "Image"; videoformat = "h264-mp4" } | ConvertTo-Json
        $iv = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 400 -Body $body
        $mp4 = @($iv.images) | Where-Object { $_ -match '\.mp4$' }
        $results["image-to-video (5B)"] = if ($mp4) { "PASS  $(@($mp4)[0])" } else { "FAIL  no mp4 in output" }
    } catch { $results["image-to-video (5B)"] = "FAIL  $($_.Exception.Message)" }
}

# 6c. Qwen-Image-Edit — instruction-based image edit (SwarmUI-native "Qwen Image Edit Plus").
#     Live-verified: model + init image + an instruction prompt edits the image (red->green apple).
Write-Host "[verify] image edit (Qwen-Image-Edit) ..."
if (-not (Test-Path (Join-Path $swModels "diffusion_models\qwen_image_edit_2511_fp8mixed.safetensors"))) {
    $results["image edit (Qwen)"] = "SKIP  (not installed; -Models full)"
} else {
    try {
        $sidQ = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $bb = @{ session_id = $sidQ; images = 1; prompt = "a single red apple on a plain white background"; model = "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors"; steps = 8; cfgscale = 1; width = 512; height = 512 } | ConvertTo-Json
        $bi = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 200 -Body $bb
        $tmpQ = Join-Path $env:TEMP "doki_qwen_base.png"
        Invoke-WebRequest "$base/$(@($bi.images)[0])" -OutFile $tmpQ -UseBasicParsing -TimeoutSec 60
        $qb64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($tmpQ))
        $eb = @{ session_id = $sidQ; images = 1; prompt = "change the apple to a green apple"; model = "qwen_image_edit_2511_fp8mixed.safetensors"; initimage = $qb64; steps = 20; cfgscale = 2.5; width = 512; height = 512 } | ConvertTo-Json
        $ei = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 400 -Body $eb
        $results["image edit (Qwen)"] = if ($ei.images) { "PASS  $(@($ei.images)[0])" } else { "FAIL  no edited image" }
    } catch { $results["image edit (Qwen)"] = "FAIL  $($_.Exception.Message)" }
}

# 6d. Music — ACE-Step 1.5 (SwarmUI-native audio model). Generates an instrumental clip.
#     Live-verified: model + style/bpm/duration -> a real MP3 (48kHz stereo).
Write-Host "[verify] music (ACE-Step 1.5) ..."
if (-not (Test-Path (Join-Path $swModels "diffusion_models\acestep_v1.5_turbo.safetensors"))) {
    $results["music (ACE-Step)"] = "SKIP  (not installed; -Models full)"
} else {
    try {
        $sidM = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $mb = @{ session_id = $sidM; images = 1; model = "acestep_v1.5_turbo.safetensors"; prompt = "[instrumental]"; textaudiostyle = "upbeat electronic, energetic synth"; textaudiobpm = 128; textaudioduration = 10; steps = 10; cfgscale = 1 } | ConvertTo-Json
        $mr = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 400 -Body $mb
        $aud = @($mr.images) | Where-Object { $_ -match '\.(mp3|flac|wav|opus)$' }
        $results["music (ACE-Step)"] = if ($aud) { "PASS  $(@($aud)[0])" } else { "FAIL  no audio output" }
    } catch { $results["music (ACE-Step)"] = "FAIL  $($_.Exception.Message)" }
}

# 6e. Upscaler — 4x-UltraSharp via SwarmUI's Refiner Upscale. Live-verified: the upscale only
#     fires when refinermethod + refinercontrolpercentage are set (control 0 = upscale, no refine).
Write-Host "[verify] upscaler (4x-UltraSharp) ..."
if (-not (Test-Path (Join-Path $swModels "upscale_models\4x-UltraSharp.pth"))) {
    $results["upscaler (4x-UltraSharp)"] = "SKIP  (not installed; -Models full)"
} else {
    try {
        $sidU = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $ub = @{ session_id = $sidU; images = 1; prompt = "a vintage pocket watch on linen, macro photo"; model = "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors"; steps = 8; cfgscale = 1; width = 512; height = 512; refinermethod = "PostApply"; refinercontrolpercentage = 0; refinerupscale = 2; refinerupscalemethod = "model-4x-UltraSharp.pth" } | ConvertTo-Json
        $ur = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 200 -Body $ub
        $utmp = Join-Path $env:TEMP "doki_upscale_verify.png"; Invoke-WebRequest "$base/$(@($ur.images)[0])" -OutFile $utmp -UseBasicParsing -TimeoutSec 60
        Add-Type -AssemblyName System.Drawing; $uim = [System.Drawing.Image]::FromFile($utmp); $uw = $uim.Width; $uim.Dispose()
        $results["upscaler (4x-UltraSharp)"] = if ($uw -ge 1024) { "PASS  ${uw}px (2x from 512)" } else { "FAIL  not upscaled (${uw}px)" }
    } catch { $results["upscaler (4x-UltraSharp)"] = "FAIL  $($_.Exception.Message)" }
}

# 6f. Fast video — LTXV-2b-0.9.8-distilled (SwarmUI-native, near-real-time, long clips).
#     Live-verified capability; this smoke runs a fast 49-frame 768x512 clip (T5 auto-downloads first run).
#     The documented recipe is 97 frames 768x512 — see docs/media-recipes.md / docs/FEATURES.md.
Write-Host "[verify] fast video (LTXV) ..."
if (-not (Test-Path (Join-Path $swModels "diffusion_models\ltxv-2b-0.9.8-distilled.safetensors"))) {
    $results["fast video (LTXV)"] = "SKIP  (not installed; -Models full)"
} else {
    try {
        $sidL = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $lb = @{ session_id = $sidL; images = 1; prompt = "a paper boat floating down a rain-soaked street gutter"; model = "ltxv-2b-0.9.8-distilled.safetensors"; textvideoframes = 49; steps = 8; cfgscale = 1; width = 768; height = 512; videofps = 24; videoformat = "h264-mp4" } | ConvertTo-Json
        $lr = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 400 -Body $lb
        $lmp4 = @($lr.images) | Where-Object { $_ -match '\.mp4$' }
        $results["fast video (LTXV)"] = if ($lmp4) { "PASS  $(@($lmp4)[0])" } else { "FAIL  no mp4" }
    } catch { $results["fast video (LTXV)"] = "FAIL  $($_.Exception.Message)" }
}

# 7. Wan -> Foley (video WITH synced audio) via the WanFoley custom workflow
Write-Host "[verify] Wan->Foley audio ..."
$foleyModel = Join-Path $root "media\SwarmUI\dlbackend\comfy\ComfyUI\models\foley\hunyuanvideo_foley.safetensors"
if (-not ((Test-Path $cwfPath) -and (Test-Path $foleyModel))) {
    $results["Wan->Foley audio"] = "SKIP  (workflow/model not installed)"
} else {
    try {
        $sid4 = (Invoke-RestMethod "$base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
        $body = @{ session_id = $sid4; images = 1; comfyuicustomworkflow = "WanFoley"; prompt = "a cat walking across a floor, paws tapping"; seed = -1 } | ConvertTo-Json
        $a = Invoke-RestMethod "$base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 900 -Body $body
        if (-not $a.images) { $results["Wan->Foley audio"] = "FAIL  no output" }
        else {
            $vp = @($a.images)[0]; $tmp = Join-Path $env:TEMP "doki_foley_verify.mp4"
            try { Invoke-WebRequest "$base/$vp" -OutFile $tmp -TimeoutSec 120 -UseBasicParsing } catch {}
            if ((Get-Command ffprobe -ErrorAction SilentlyContinue) -and (Test-Path $tmp)) {
                $streams = & ffprobe -v error -select_streams a -show_entries stream=codec_type -of csv=p=0 $tmp 2>$null
                $results["Wan->Foley audio"] = if ("$streams" -match "audio") { "PASS  $vp (audio stream present)" } else { "FAIL  $vp (no audio stream)" }
            } else {
                $sz = if (Test-Path $tmp) { (Get-Item $tmp).Length } else { 0 }
                $results["Wan->Foley audio"] = if ($sz -gt 10000) { "PASS  $vp (ffprobe absent; $([math]::Round($sz/1KB))KB MP4)" } else { "FAIL  $vp (empty/unfetched)" }
            }
        }
    } catch { $results["Wan->Foley audio"] = "FAIL  $($_.Exception.Message)" }
}

# restore default resting state
Doki up agent

$pass = 0; $fail = 0; $skip = 0
foreach ($k in $results.Keys) {
    $v = $results[$k]
    if ($v -like "PASS*") { $pass++ } elseif ($v -like "SKIP*") { $skip++ } else { $fail++ }
}

if ($Json) {
    $payload = [ordered]@{ checks = $results; summary = [ordered]@{ passed = $pass; failed = $fail; skipped = $skip; total = $results.Count } }
    $payload | ConvertTo-Json -Depth 5
} else {
    Write-Host ""
    Write-Host "=== Verify results ===" -ForegroundColor Cyan
    foreach ($k in $results.Keys) {
        $v = $results[$k]
        $color = if ($v -like "PASS*") { "Green" } elseif ($v -like "SKIP*") { "DarkGray" } else { "Red" }
        Write-Host ("  {0,-24} {1}" -f $k, $v) -ForegroundColor $color
    }
    Write-Host ""
    $summary = "$pass passed, $fail failed" + $(if ($skip) { ", $skip skipped" } else { "" }) + " (of $($results.Count) checks)"
    Write-Host $summary -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Yellow" })
}
exit $(if ($fail -eq 0) { 0 } else { 1 })
