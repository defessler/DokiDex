# tests/doki-gen.test.ps1 — unit tests for the GPU-free core of `doki gen` (serving/doki-gen.ps1):
# switches -> kind, the docs/wiki/11-media-recipes.md recipe table, prompt placement, and final-body assembly.
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

# --- Get-GenRecipe: the docs/wiki/11-media-recipes.md table, 1:1 ---
$img = Get-GenRecipe -Kind image
Assert ($img.model -eq 'z_image_bf16.safetensors')                 "image -> Z-Image Base model (quality default)"
Assert ($img.steps -eq 35 -and $img.width -eq 1024)                "image -> 35 steps @ 1024"
Assert ($img.cfgscale -eq 4.5 -and $img.negativeprompt)            "image -> CFG 4.5 + active negative prompt"
Assert ($img.sampler -eq 'dpmpp_2m' -and $img.scheduler -eq 'karras') "image -> dpmpp_2m / karras"
Assert (-not $img.ContainsKey('refinermethod'))                    "image (no -Upscale/-Refine) -> no refiner fields"
$imgFast = Get-GenRecipe -Kind image -Fast
Assert ($imgFast.model -eq 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors') "image -Fast -> Z-Image Turbo model"
Assert ($imgFast.steps -eq 8 -and $imgFast.cfgscale -eq 1)         "image -Fast -> 8 steps / CFG 1 (Turbo)"

$vid = Get-GenRecipe -Kind video
Assert ($vid.model -eq 'wan2.2_ti2v_5B_fp16.safetensors')          "video -> Wan 2.2 5B"
Assert ($vid.videoformat -eq 'h264-mp4' -and $vid.textvideoframes -eq 49) "video -> mp4 / 49 frames"
Assert ($vid.sigmashift -eq 8 -and $vid.sampler -eq 'uni_pc')      "video -> sigma shift 8 + uni_pc (Wan 2.2 5B tuned)"
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
Assert ($i2v.videomodel -eq 'wan2.2_ti2v_5B_fp16.safetensors' -and $i2v.videoframes -eq 49) "i2v -> videomodel + 49 frames (animate)"
Assert ($i2v.videoresolution -eq 'Image' -and $i2v.videosteps -eq 20) "i2v -> videoresolution=Image + videosteps (the I2V trigger trio)"
$fol = Get-GenRecipe -Kind foley
Assert ($fol.comfyuicustomworkflow -eq 'WanFoley' -and $fol.seed -eq -1) "foley -> WanFoley custom workflow + seed -1"

$up = Get-GenRecipe -Kind image -Upscale
Assert ($up.refinermethod -eq 'PostApply' -and $up.refinerupscalemethod -eq 'model-4x-UltraSharp.pth') "image -Upscale -> 4x-UltraSharp refiner"
Assert ($up.refinercontrolpercentage -eq 0)                        "image -Upscale -> control% 0 (pure upscale)"
$rf = Get-GenRecipe -Kind image -Refine
Assert ($rf.refinercontrolpercentage -eq 0.35 -and $rf.refinerdotiling -eq $true) "image -Refine -> control% 0.35 + tiling (hi-res-fix)"

# --- Get-GenPromptFields: idea placement per kind ---
$pf = Get-GenPromptFields -Kind image -Idea 'a cat on a skateboard'
Assert ($pf.prompt -eq '<mpprompt:a cat on a skateboard>')         "image idea -> <mpprompt:..> (rewriter expand)"
Assert ((Get-GenPromptFields -Kind image -Idea 'a cat' -Raw).prompt -eq 'a cat') "image -Raw -> literal prompt"
$pfM = Get-GenPromptFields -Kind music -Idea 'upbeat synthwave'
Assert ($pfM.prompt -eq '[instrumental]' -and $pfM.textaudiostyle -eq 'upbeat synthwave') "music -> [instrumental] + style"
Assert ((Get-GenPromptFields -Kind edit -Idea 'make the apple green').prompt -eq 'make the apple green') "edit -> literal instruction"

# --- Get-GenPromptFields: -Face / -Realism SwarmUI tags (opt-in, image/edit/i2v; outside <mpprompt:..>) ---
Assert (-not ($pf.prompt -match '<segment:face' -or $pf.prompt -match '<lora:')) "plain image prompt -> no <segment:face> / <lora:> (opt-in)"
$pfFace = Get-GenPromptFields -Kind image -Idea 'a cat' -Face
Assert ($pfFace.prompt -match '<segment:face')                     "-Face -> appends <segment:face,..> tag"
Assert ($pfFace.prompt -like '<mpprompt:a cat>*')                  "-Face -> tag rides AFTER the <mpprompt:..> wrapper"
$pfReal = Get-GenPromptFields -Kind image -Idea 'a cat' -Realism
Assert ($pfReal.prompt -match '<lora:')                            "-Realism -> appends <lora:..:0.7> tag"
Assert ($pfReal.prompt -match '<lora:Z-Image-Realism:0\.7>')       "-Realism -> the Z-Image realism LoRA at 0.7"
$pfBoth = Get-GenPromptFields -Kind edit -Idea 'fix it' -Face -Realism
Assert ($pfBoth.prompt -match '<lora:' -and $pfBoth.prompt -match '<segment:face') "edit -Face -Realism -> both tags after the literal instruction"
# music/video are NOT image-family: tags must never leak onto them
Assert (-not ((Get-GenPromptFields -Kind music -Idea 'x' -Face -Realism).prompt -match '<segment:face|<lora:')) "music -Face/-Realism -> no SwarmUI tags (not image-family)"

# --- Build-GenBody: recipe + prompt + session merge; init image opt-in ---
$body = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 'sess-123'
Assert ($body.session_id -eq 'sess-123' -and $body.images -eq 1)   "body carries session_id + images=1"
Assert ($body.model -eq 'z_image_bf16.safetensors' -and $body.prompt -eq 'x') "body merges recipe + prompt"
Assert (-not $body.ContainsKey('initimage'))                       "body has no initimage unless supplied"
$bodyI = Build-GenBody -Recipe (Get-GenRecipe -Kind edit) -PromptFields (Get-GenPromptFields -Kind edit -Idea 'y') -SessionId 's' -InitImageB64 'BASE64DATA'
Assert ($bodyI.initimage -eq 'BASE64DATA' -and $bodyI.initimagecreativity -eq 0) "init image -> initimage + creativity 0"

Write-Host "`ndoki-gen: $script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
exit $(if ($script:fail) { 1 } else { 0 })
