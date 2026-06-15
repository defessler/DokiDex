# tests/doki-gen.test.ps1 — unit tests for the GPU-free core of `doki gen` (serving/doki-gen.ps1):
# switches -> kind, the docs/media-recipes.md recipe table, prompt placement, and final-body assembly.
# No SwarmUI, no GPU, no network (Invoke-Gen's live POST is exercised by `doki verify`, not here).
# Framework-free: exit 0 = pass, 1 = fail.

$ErrorActionPreference = "Stop"
$gen = Join-Path $PSScriptRoot "..\serving\doki-gen.ps1"
if (-not (Test-Path $gen)) { Write-Error "doki-gen.ps1 not found at $gen"; exit 2 }
. $gen

$script:pass = 0; $script:fail = 0
function Assert($cond, $msg) {
    if ($cond) { $script:pass++; Write-Host "  [PASS] $msg" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
}

# --- Resolve-GenKind: switches -> exactly one kind; default image; ambiguity guard ---
Assert ((Resolve-GenKind) -eq 'image')        "no switch -> image (default)"
Assert ((Resolve-GenKind -Video) -eq 'video') "-Video -> video"
Assert ((Resolve-GenKind -Music) -eq 'music') "-Music -> music"
Assert ((Resolve-GenKind -Edit)  -eq 'edit')  "-Edit  -> edit"
Assert ((Resolve-GenKind -I2v)   -eq 'i2v')   "-I2v   -> i2v"
Assert ((Resolve-GenKind -Foley) -eq 'foley') "-Foley -> foley"
$ambiguous = $false
try { Resolve-GenKind -Video -Music | Out-Null } catch { $ambiguous = $true }
Assert $ambiguous                             "-Video -Music -> throws (ambiguous)"

# --- Get-GenRecipe: the media-recipes.md table, 1:1 ---
$img = Get-GenRecipe -Kind image
Assert ($img.model -eq 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors') "image -> Z-Image Turbo model"
Assert ($img.steps -eq 8 -and $img.width -eq 1024)                 "image -> 8 steps @ 1024"
Assert (-not $img.ContainsKey('refinermethod'))                    "image (no -Upscale) -> no refiner fields"
Assert ((Get-GenRecipe -Kind image -Fast).steps -eq 6)             "image -Fast -> fewer steps (6)"

$vid = Get-GenRecipe -Kind video
Assert ($vid.model -eq 'wan2.2_ti2v_5B_fp16.safetensors')          "video -> Wan 2.2 5B"
Assert ($vid.videoformat -eq 'h264-mp4' -and $vid.textvideoframes -eq 49) "video -> mp4 / 49 frames"
$vidFast = Get-GenRecipe -Kind video -Fast
Assert ($vidFast.model -eq 'ltxv-2b-0.9.8-distilled.safetensors')  "video -Fast -> LTXV distilled"
Assert ($vidFast.steps -eq 8)                                      "video -Fast -> 8 steps"

$mus = Get-GenRecipe -Kind music
Assert ($mus.model -eq 'acestep_v1.5_turbo.safetensors')           "music -> ACE-Step"
Assert ($mus.textaudioduration -eq 10)                             "music -> 10s default"

$edt = Get-GenRecipe -Kind edit
Assert ($edt.model -eq 'qwen_image_edit_2511_fp8mixed.safetensors') "edit -> Qwen-Image-Edit"
Assert ($edt.cfgscale -eq 2.5)                                     "edit -> cfg 2.5"

$i2v = Get-GenRecipe -Kind i2v
Assert ($i2v.videomodel -eq 'wan2.2_ti2v_5B_fp16.safetensors' -and $i2v.videoframes -eq 25) "i2v -> videomodel + 25 frames (animate)"
Assert ($i2v.videoresolution -eq 'Image' -and $i2v.videosteps -eq 20) "i2v -> videoresolution=Image + videosteps (the I2V trigger trio)"
$fol = Get-GenRecipe -Kind foley
Assert ($fol.comfyuicustomworkflow -eq 'WanFoley' -and $fol.seed -eq -1) "foley -> WanFoley custom workflow + seed -1"

$up = Get-GenRecipe -Kind image -Upscale
Assert ($up.refinermethod -eq 'PostApply' -and $up.refinerupscalemethod -eq 'model-4x-UltraSharp.pth') "image -Upscale -> 4x-UltraSharp refiner"
Assert ($up.refinercontrolpercentage -eq 0)                        "image -Upscale -> control% 0 (pure upscale)"

# --- Get-GenPromptFields: idea placement per kind ---
$pf = Get-GenPromptFields -Kind image -Idea 'a cat on a skateboard'
Assert ($pf.prompt -eq '<mpprompt:a cat on a skateboard>')         "image idea -> <mpprompt:..> (rewriter expand)"
Assert ((Get-GenPromptFields -Kind image -Idea 'a cat' -Raw).prompt -eq 'a cat') "image -Raw -> literal prompt"
$pfM = Get-GenPromptFields -Kind music -Idea 'upbeat synthwave'
Assert ($pfM.prompt -eq '[instrumental]' -and $pfM.textaudiostyle -eq 'upbeat synthwave') "music -> [instrumental] + style"
Assert ((Get-GenPromptFields -Kind edit -Idea 'make the apple green').prompt -eq 'make the apple green') "edit -> literal instruction"

# --- Build-GenBody: recipe + prompt + session merge; init image opt-in ---
$body = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 'sess-123'
Assert ($body.session_id -eq 'sess-123' -and $body.images -eq 1)   "body carries session_id + images=1"
Assert ($body.model -eq 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors' -and $body.prompt -eq 'x') "body merges recipe + prompt"
Assert (-not $body.ContainsKey('initimage'))                       "body has no initimage unless supplied"
$bodyI = Build-GenBody -Recipe (Get-GenRecipe -Kind edit) -PromptFields (Get-GenPromptFields -Kind edit -Idea 'y') -SessionId 's' -InitImageB64 'BASE64DATA'
Assert ($bodyI.initimage -eq 'BASE64DATA' -and $bodyI.initimagecreativity -eq 0) "init image -> initimage + creativity 0"

Write-Host "`ndoki-gen: $script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
exit $(if ($script:fail) { 1 } else { 0 })
