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

# restore default resting state
Doki up agent

Write-Host ""
Write-Host "=== Verify results ===" -ForegroundColor Cyan
$pass = 0
foreach ($k in $results.Keys) {
    $ok = $results[$k] -like "PASS*"
    if ($ok) { $pass++ }
    Write-Host ("  {0,-22} {1}" -f $k, $results[$k]) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
}
Write-Host ""
Write-Host "$pass/$($results.Count) capabilities verified" -ForegroundColor $(if ($pass -eq $results.Count) { "Green" } else { "Yellow" })
exit $(if ($pass -eq $results.Count) { 0 } else { 1 })
