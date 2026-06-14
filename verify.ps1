# verify.ps1 — full-stack functional smoke test for DokiCode.
# Cycles the doki modes and checks each capability with a REAL API call:
#   chat/code (:8080) · autocomplete (:8012) · image + video (:7801)
# Restores agent mode at the end. Run via:  .\verify.ps1   or   .\doki.ps1 verify
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$results = [ordered]@{}
function Doki($cmd, $arg) { & (Join-Path $root "doki.ps1") $cmd $arg | Out-Null }
function Probe($u) { try { Invoke-WebRequest $u -TimeoutSec 5 -UseBasicParsing | Out-Null; $true } catch { $false } }

Write-Host "=== DokiCode full-stack verify ===" -ForegroundColor Cyan

# 1. chat / code — agent mode, real chat completion
Write-Host "[verify] chat/code ..."
Doki up agent
try {
    $body = @{ model = "coder-fast"; messages = @(@{ role = "user"; content = "Reply with exactly: OK" }); max_tokens = 10; temperature = 0 } | ConvertTo-Json -Depth 5
    $r = Invoke-RestMethod "http://127.0.0.1:8080/v1/chat/completions" -Method Post -ContentType "application/json" -TimeoutSec 180 -Body $body
    $results["chat/code (:8080)"] = if ($r.choices[0].message.content) { "PASS  '$($r.choices[0].message.content.Trim())'" } else { "FAIL  empty response" }
} catch { $results["chat/code (:8080)"] = "FAIL  $($_.Exception.Message)" }

# 2. autocomplete — coexist mode, FIM infill
Write-Host "[verify] autocomplete ..."
Doki up coexist
try {
    $body = @{ input_prefix = "def add(a, b):`n    return "; input_suffix = ""; n_predict = 8; temperature = 0 } | ConvertTo-Json
    $r = Invoke-RestMethod "http://127.0.0.1:8012/infill" -Method Post -ContentType "application/json" -TimeoutSec 120 -Body $body
    $results["autocomplete (:8012)"] = if ($r.content) { "PASS  '$($r.content.Trim())'" } else { "FAIL  empty" }
} catch { $results["autocomplete (:8012)"] = "FAIL  $($_.Exception.Message)" }

# 3 + 4. image + video — media mode
Write-Host "[verify] image + video (this brings up SwarmUI; first gens load the models) ..."
Doki up media
for ($i = 0; $i -lt 60 -and -not (Probe "http://127.0.0.1:7801/"); $i++) { Start-Sleep 1 }
$base = "http://127.0.0.1:7801"
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
    $results["video gen (Wan 1.3B)"] = if ($vid.images) { "PASS  $(@($vid.images)[0])" } else { "FAIL  no video" }
} catch { $results["video gen (Wan 1.3B)"] = "FAIL  $($_.Exception.Message)" }

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

Write-Host ""
Write-Host "=== Verify results ===" -ForegroundColor Cyan
$pass = 0; $fail = 0
foreach ($k in $results.Keys) {
    $v = $results[$k]
    $color = if ($v -like "PASS*") { "Green" } elseif ($v -like "SKIP*") { "DarkGray" } else { "Red" }
    if ($v -like "PASS*") { $pass++ } elseif ($v -notlike "SKIP*") { $fail++ }
    Write-Host ("  {0,-24} {1}" -f $k, $v) -ForegroundColor $color
}
Write-Host ""
Write-Host "$pass passed, $fail failed (of $($results.Count) checks)" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Yellow" })
exit $(if ($fail -eq 0) { 0 } else { 1 })
