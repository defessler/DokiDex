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
Assert ((Resolve-GenKind -FaceId) -eq 'faceid') "-FaceId -> faceid (InstantID face-identity)"
$ambiguous = $false
try { Resolve-GenKind -Video -Music | Out-Null } catch { $ambiguous = $true }
Assert $ambiguous                             "-Video -Music -> throws (ambiguous)"
$ambiguous2 = $false
try { Resolve-GenKind -FaceId -Foley | Out-Null } catch { $ambiguous2 = $true }
Assert $ambiguous2                            "-FaceId -Foley -> throws (ambiguous)"

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

# video -Quality = the GATED Wan 2.2 A14B GGUF dual-expert tier (quality video). SwarmUI wires the two
# noise experts via its image-refiner StepSwap: base = HIGH-noise expert, Refiner Model = LOW-noise expert,
# RefinerMethod=StepSwap, RefinerControlPercentage=0.5 (the high expert runs only the first ~50% of steps).
# The dual-expert STRUCTURE is doc-sourced from SwarmUI's docs/Video Model Support.md; the refinermodel body
# key is derived (CleanTypeName of "Refiner Model") and steps/sampler are on-GPU (see doki-gen.ps1 comment).
# Like image/video, -Fast wins over -Quality; the default (no switch) stays the 5B byte-for-byte (pinned above).
$vidQ = Get-GenRecipe -Kind video -Quality
Assert ($vidQ.model -eq 'Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf')   "video -Quality -> Wan 2.2 A14B HIGH-noise GGUF (base expert)"
Assert ($vidQ.refinermethod -eq 'StepSwap')                        "video -Quality -> RefinerMethod=StepSwap (noise-level step-swap)"
Assert ($vidQ.refinermodel -eq 'Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf') "video -Quality -> Refiner Model = LOW-noise GGUF (low expert)"
Assert ($vidQ.refinercontrolpercentage -eq 0.5)                    "video -Quality -> Refiner Control% = 0.5 (high expert does first ~50% of steps)"
# StepSwap here is a noise-EXPERT handoff (a denoising step-swap), NOT the hi-res upscale the image
# -Upscale/-Refine path reuses the same refiner group for — so the refiner must NOT inherit an upscale
# factor: refinerupscale=1 (explicit, no resize), unlike the image path's refinerupscale=2.
Assert ($vidQ.refinerupscale -eq 1)                                "video -Quality -> Refiner Upscale = 1 (StepSwap is a noise handoff, not a hi-res upscale)"
Assert ($vidQ.cfgscale -eq 5)                                      "video -Quality -> CFG 5 (SwarmUI doc reference for T2V 14B)"
Assert ($vidQ.sigmashift -eq 8)                                    "video -Quality -> sigma shift 8 (carried from the 5B / doc default)"
Assert ($vidQ.textvideoframes -eq 49 -and $vidQ.videofps -eq 24)   "video -Quality -> 49 frames @ 24fps"
Assert ($vidQ.width -eq 832 -and $vidQ.height -eq 480)             "video -Quality -> 832x480"
Assert ($vidQ.videoformat -eq 'h264-mp4')                          "video -Quality -> h264-mp4"
# -Fast wins over -Quality (the precedence the arm encodes: if($Fast) first, elseif($Quality))
Assert ((Get-GenRecipe -Kind video -Fast -Quality).model -eq 'ltxv-2b-0.9.8-distilled.safetensors') "video -Fast -Quality -> -Fast wins (LTXV)"
# the DEFAULT video path is byte-for-byte unchanged by adding the -Quality arm (no refiner on the 5B default)
Assert (-not $vid.ContainsKey('refinermethod'))                    "video (default) -> no refiner fields (5B unchanged)"
Assert (-not $vid.ContainsKey('refinermodel'))                     "video (default) -> no refinermodel (5B unchanged)"

# music DEFAULT = ACE-Step turbo (fast, 10 steps / cfg 1). -Quality is the opt-in hi-fi swap (xl_base);
# unlike image/video, turbo is the music DEFAULT and quality is opt-in (inverted -Fast semantics) -> a
# dedicated -Quality switch, so the existing turbo default stays byte-for-byte unchanged.
$mus = Get-GenRecipe -Kind music
Assert ($mus.model -eq 'acestep_v1.5_turbo.safetensors')           "music -> ACE-Step turbo (fast default)"
Assert ($mus.textaudioduration -eq 10)                             "music -> 10s default"
Assert ($mus.steps -eq 10 -and $mus.cfgscale -eq 1)                "music (default) -> 10 steps / CFG 1 (turbo)"
Assert (-not $mus.ContainsKey('sampler'))                          "music (default) -> no sampler key (turbo unchanged)"
# -Quality -> ACE-Step 1.5 XL base with the OFFICIAL ComfyUI example-workflow params (steps 50 / cfg 6 /
# euler / simple); bpm + duration defaults are preserved (still -Bpm/-Duration overridable downstream).
$musQ = Get-GenRecipe -Kind music -Quality
Assert ($musQ.model -eq 'acestep_v1.5_xl_base_bf16.safetensors')   "music -Quality -> ACE-Step 1.5 XL base"
Assert ($musQ.steps -eq 50 -and $musQ.cfgscale -eq 6)              "music -Quality -> 50 steps / CFG 6 (official XL base)"
Assert ($musQ.sampler -eq 'euler' -and $musQ.scheduler -eq 'simple') "music -Quality -> euler / simple"
Assert ($musQ.textaudiobpm -eq 128 -and $musQ.textaudioduration -eq 10) "music -Quality -> keeps 128bpm / 10s defaults"

$edt = Get-GenRecipe -Kind edit
Assert ($edt.model -eq 'qwen_image_edit_2511_fp8mixed.safetensors') "edit -> Qwen-Image-Edit"
Assert ($edt.cfgscale -eq 2.5)                                     "edit -> cfg 2.5"

$i2v = Get-GenRecipe -Kind i2v
Assert ($i2v.videomodel -eq 'wan2.2_ti2v_5B_fp16.safetensors' -and $i2v.videoframes -eq 49) "i2v -> videomodel + 49 frames (animate)"
Assert ($i2v.videoresolution -eq 'Image' -and $i2v.videosteps -eq 20) "i2v -> videoresolution=Image + videosteps (the I2V trigger trio)"
$fol = Get-GenRecipe -Kind foley
Assert ($fol.comfyuicustomworkflow -eq 'WanFoley' -and $fol.seed -eq -1) "foley -> WanFoley custom workflow + seed -1"
# faceid = the InstantID custom-workflow alias (SDXL face-identity). Maps to comfyuicustomworkflow=InstantID;
# the reference face rides the init-image channel (doki gen -FaceId -InitImage <face.png>), NOT a new key.
$fid = Get-GenRecipe -Kind faceid
Assert ($fid.comfyuicustomworkflow -eq 'InstantID') "faceid -> InstantID custom workflow (the face-identity alias)"
Assert (-not $fid.ContainsKey('useipadapterforrevision')) "faceid -> does NOT set SwarmUI's IP-Adapter-revision flag (the face rides init-image, a different path)"

# --- Invoke-Gen: -FaceId (InstantID) REQUIRES -InitImage up front (mirrors -Edit's init-image guard) ---
# InstantID is meaningless without a reference face, which rides the init-image channel. The guard must fire
# loudly BEFORE any SwarmUI contact (same as -Edit), so `doki gen -FaceId 'portrait'` with no -InitImage throws
# a clear "requires -InitImage" error. GPU/network-free: the throw path returns before the SwarmUI probe, and
# the positive path uses a real temp file + -BodyOnly (stops before any /API call).
$faceidNoInit = $false; $faceidErr = $null
try { Invoke-Gen -Prompt 'portrait of a hero' -Kind faceid | Out-Null } catch { $faceidNoInit = $true; $faceidErr = "$($_.Exception.Message)" }
Assert $faceidNoInit                                       "-FaceId with NO -InitImage -> throws (a reference face is mandatory)"
Assert ($faceidErr -match 'requires\s+-InitImage')         "-FaceId missing -InitImage -> the error names 'requires -InitImage' (clear, up-front)"
$faceTmp = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-faceid-$([guid]::NewGuid().ToString('N')).png"
Set-Content -LiteralPath $faceTmp -Value 'not-a-real-image-just-bytes' -NoNewline
$faceidOk = $true
try { $fb = Invoke-Gen -Prompt 'portrait of a hero' -Kind faceid -InitImage $faceTmp -BodyOnly } catch { $faceidOk = $false }
Assert $faceidOk                                           "-FaceId WITH -InitImage -> does NOT throw the init-image guard (reference face supplied)"
$fbBody = $null; try { $fbBody = $fb | ConvertFrom-Json } catch {}
Assert ($fbBody -and $fbBody.comfyuicustomworkflow -eq 'InstantID') "-FaceId -InitImage -BodyOnly -> body carries comfyuicustomworkflow=InstantID + the init image"
Remove-Item -LiteralPath $faceTmp -Force -ErrorAction SilentlyContinue

$up = Get-GenRecipe -Kind image -Upscale
Assert ($up.refinermethod -eq 'PostApply' -and $up.refinerupscalemethod -eq 'model-4x-UltraSharp.pth') "image -Upscale -> 4x-UltraSharp refiner"
Assert ($up.refinercontrolpercentage -eq 0)                        "image -Upscale -> control% 0 (pure upscale)"
$rf = Get-GenRecipe -Kind image -Refine
Assert ($rf.refinercontrolpercentage -eq 0.35 -and $rf.refinerdotiling -eq $true) "image -Refine -> control% 0.35 + tiling (hi-res-fix)"

# --- upscale engine selector: -Upscaler picks the refiner upscale model (default unchanged; raw name verbatim) ---
Assert ((Get-GenRecipe -Kind image -Upscale).refinerupscalemethod -eq 'model-4x-UltraSharp.pth') "-Upscale (no engine) -> 4x-UltraSharp (unchanged default)"
Assert ((Get-GenRecipe -Kind image -Upscale -Upscaler anime).refinerupscalemethod -eq '4x-AnimeSharp.pth') "-Upscaler anime -> AnimeSharp upscaler"
Assert ((Get-GenRecipe -Kind image -Refine -Upscaler 'my-custom.pth').refinerupscalemethod -eq 'my-custom.pth') "-Upscaler <file> -> used verbatim (any installed upscaler)"
Assert (-not (Get-GenRecipe -Kind image -Upscaler anime).ContainsKey('refinerupscalemethod')) "-Upscaler without -Upscale/-Refine -> no refiner (gated)"

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

# --- LoRA mixer: -Lora "name:weight,name" -> <lora:name:weight> tags (image-family only; bare name -> 1) ---
# --- promptable segmentation: -Segment "kw, kw:creativity" -> <segment:kw,creativity,0.5> (generalizes -Face) ---
$pfSeg = Get-GenPromptFields -Kind image -Idea 'a knight' -Segment 'sword, hands:0.6'
Assert ($pfSeg.prompt -match '<segment:sword,0\.4,0\.5>')         "-Segment keyword -> <segment:kw,0.4,0.5> (default creativity)"
Assert ($pfSeg.prompt -match '<segment:hands,0\.6,0\.5>')         "-Segment kw:creativity -> overrides creativity"
Assert (-not ((Get-GenPromptFields -Kind music -Idea 'x' -Segment 'a').prompt -match '<segment:')) "-Segment ignored on non-image-family (music)"

$pfLora = Get-GenPromptFields -Kind image -Idea 'a cat' -Lora 'anime-style:0.8, detail-boost'
Assert ($pfLora.prompt -match '<lora:anime-style:0\.8>')          "-Lora name:weight -> <lora:name:weight>"
Assert ($pfLora.prompt -match '<lora:detail-boost:1>')            "-Lora bare name -> weight defaults to 1"
$pfLoraEdit = Get-GenPromptFields -Kind edit -Idea 'fix' -Lora 'subdir/foo:0.5'
Assert ($pfLoraEdit.prompt -match '<lora:subdir/foo:0\.5>')       "-Lora supports a subdir-relative name"
Assert (-not ((Get-GenPromptFields -Kind music -Idea 'x' -Lora 'a:0.5').prompt -match '<lora:')) "-Lora ignored on non-image-family kinds (music)"
Assert (-not ($pf.prompt -match '<lora:'))                        "no -Lora -> no <lora:> tag (opt-in)"
# music/video are NOT image-family: tags must never leak onto them
Assert (-not ((Get-GenPromptFields -Kind music -Idea 'x' -Face -Realism).prompt -match '<segment:face|<lora:')) "music -Face/-Realism -> no SwarmUI tags (not image-family)"

# --- Build-GenBody: recipe + prompt + session merge; init image opt-in ---
$body = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 'sess-123'
Assert ($body.session_id -eq 'sess-123' -and $body.images -eq 1)   "body carries session_id + images=1"
Assert ($body.model -eq 'z_image_bf16.safetensors' -and $body.prompt -eq 'x') "body merges recipe + prompt"
Assert (-not $body.ContainsKey('initimage'))                       "body has no initimage unless supplied"
$bodyI = Build-GenBody -Recipe (Get-GenRecipe -Kind edit) -PromptFields (Get-GenPromptFields -Kind edit -Idea 'y') -SessionId 's' -InitImageB64 'BASE64DATA'
Assert ($bodyI.initimage -eq 'BASE64DATA' -and $bodyI.initimagecreativity -eq 0) "init image -> initimage + creativity 0"

# --- ControlNet stacking (SwarmUI-source-confirmed keys via CleanTypeName): units -> controlnet/two/three ---
$cnJson = '[{"Model":"canny.safetensors","Image":"CTRLB64","Strength":0.8,"Preprocessor":"canny"},{"Model":"depth.safetensors","Image":"D2","Strength":0.5,"Preprocessor":"depth"}]'
$bCN = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -ControlNets $cnJson
Assert ($bCN.controlnetmodel -eq 'canny.safetensors' -and $bCN.controlnetstrength -eq 0.8) "ControlNet unit 1 -> controlnetmodel + strength"
Assert ($bCN.controlnetimageinput -eq 'CTRLB64' -and $bCN.controlnetpreprocessor -eq 'canny') "ControlNet unit 1 -> imageinput + preprocessor"
Assert ($bCN.controlnettwomodel -eq 'depth.safetensors' -and $bCN.controlnettwopreprocessor -eq 'depth') "ControlNet unit 2 -> controlnettwo* (stacking)"
$bNoCN = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's'
Assert (-not $bNoCN.ContainsKey('controlnetmodel'))                  "no ControlNets -> no controlnet* params (opt-in)"

# --- FLF2V end keyframe: "Video End Image" -> videoendimage (SwarmUI source-confirmed key) ---
$bEnd = Build-GenBody -Recipe (Get-GenRecipe -Kind video) -PromptFields (Get-GenPromptFields -Kind video -Idea 'x') -SessionId 's' -EndImageB64 'ENDB64'
Assert ($bEnd.videoendimage -eq 'ENDB64')                           "end image -> videoendimage"
$bNoEnd = Build-GenBody -Recipe (Get-GenRecipe -Kind video) -PromptFields (Get-GenPromptFields -Kind video -Idea 'x') -SessionId 's'
Assert (-not $bNoEnd.ContainsKey('videoendimage'))                  "no end image -> no videoendimage (opt-in)"

# --- IP-Adapter image reference: "Use IP-Adapter"/"IP-Adapter Weight" -> useipadapterforrevision/ipadapterweight ---
$bRef = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -InitImageB64 'REFB64' -Reference $true -RefWeight 0.7
Assert ($bRef.useipadapterforrevision -eq $true -and $bRef.ipadapterweight -eq 0.7) "-Reference + init -> useipadapterforrevision + ipadapterweight"
$bRefNoInit = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Reference $true
Assert (-not $bRefNoInit.ContainsKey('useipadapterforrevision'))    "-Reference without an init image -> no-op (needs the reference image)"

# --- frame interpolation: "Video Frame Interpolation Method/Multiplier" -> videoframeinterpolation* (source) ---
$bIp = Build-GenBody -Recipe (Get-GenRecipe -Kind video) -PromptFields (Get-GenPromptFields -Kind video -Idea 'x') -SessionId 's' -Interpolate 'RIFE' -InterpolateMult 4
Assert ($bIp.videoframeinterpolationmethod -eq 'RIFE' -and $bIp.videoframeinterpolationmultiplier -eq 4) "-Interpolate -> videoframeinterpolationmethod + multiplier"
$bNoIp = Build-GenBody -Recipe (Get-GenRecipe -Kind video) -PromptFields (Get-GenPromptFields -Kind video -Idea 'x') -SessionId 's'
Assert (-not $bNoIp.ContainsKey('videoframeinterpolationmethod'))    "no -Interpolate -> no interpolation params (opt-in)"

# --- custom ComfyUI workflow runner: -Workflow <name> -> comfyuicustomworkflow (the foley hook, generalized) ---
$bWf = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Workflow 'SUPIR'
Assert ($bWf.comfyuicustomworkflow -eq 'SUPIR')                      "-Workflow -> comfyuicustomworkflow (runs any installed workflow: SUPIR/InstantID/own)"
$bNoWf = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's'
Assert (-not $bNoWf.ContainsKey('comfyuicustomworkflow'))           "no -Workflow -> no custom workflow (opt-in)"

# --- seamless tile: -Tile true/x/y -> seamlesstileable (SwarmUI enum); opt-in ---
Assert ((Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Tile 'true').seamlesstileable -eq 'true') "-Tile true -> seamlesstileable=true"
Assert ((Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Tile 'x').seamlesstileable -eq 'X-Only') "-Tile x -> seamlesstileable=X-Only"
Assert (-not (Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's').ContainsKey('seamlesstileable')) "no -Tile -> no seamlesstileable (opt-in)"

# --- model override: -Model replaces the recipe's default checkpoint; default kept otherwise ---
Assert ((Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'Chroma1-HD.safetensors').model -eq 'Chroma1-HD.safetensors') "-Model overrides body.model"
Assert ((Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's').model -eq (Get-GenRecipe -Kind image).model) "no -Model -> recipe default model kept"

# --- FLUX.2 Klein family override: a FILENAME-keyed PURE map applied AFTER the -Model swap ---
# Pure function contract: a FLUX.2 Klein checkpoint -> the official ComfyUI-template params
# (euler + the Flux2 specialty scheduler; distilled vs base steps/cfg); EVERY other/empty model -> {}.
# This is the ONLY thing that may change a non-Z-Image path, and it fires ONLY for flux-2-klein* models.
Assert ((Get-ModelFamilyOverride '').Count -eq 0)                  "family override: empty model -> no override"
Assert ((Get-ModelFamilyOverride $null).Count -eq 0)              "family override: null model -> no override"
Assert ((Get-ModelFamilyOverride 'z_image_bf16.safetensors').Count -eq 0) "family override: Z-Image -> no override (unchanged)"
Assert ((Get-ModelFamilyOverride 'Chroma1-HD-fp8mixed-final.safetensors').Count -eq 0) "family override: Chroma -> no override (unchanged)"
Assert ((Get-ModelFamilyOverride 'Illustrious-XL-v1.0.safetensors').Count -eq 0) "family override: anime SDXL -> no override (unchanged)"
$ovD = Get-ModelFamilyOverride 'flux-2-klein-4b.safetensors'
Assert ($ovD.steps -eq 4 -and $ovD.cfgscale -eq 1)               "family override: Klein DISTILLED -> 4 steps / cfg 1 (template-exact)"
Assert ($ovD.sampler -eq 'euler' -and $ovD.scheduler -eq 'Flux2') "family override: Klein DISTILLED -> euler / Flux2 scheduler"
$ovB = Get-ModelFamilyOverride 'flux-2-klein-base-4b.safetensors'
Assert ($ovB.steps -eq 20 -and $ovB.cfgscale -eq 5)              "family override: Klein BASE -> 20 steps / cfg 5 (template-exact)"
Assert ($ovB.sampler -eq 'euler' -and $ovB.scheduler -eq 'Flux2') "family override: Klein BASE -> euler / Flux2 scheduler"
Assert ((Get-ModelFamilyOverride 'FLUX-2-KLEIN-4B.SAFETENSORS').sampler -eq 'euler') "family override: case-insensitive filename match"

# -Kind guard (defense-in-depth): the FLUX.2 sampler knobs are an IMAGE-recipe override only. A non-image
# kind (e.g. a hand-typed `doki gen -Edit -Model flux-2-klein-4b.safetensors`) must NOT get the override —
# otherwise it would clobber the edit recipe's steps/cfg/sampler. Image kind (the default) is unaffected.
Assert ((Get-ModelFamilyOverride 'flux-2-klein-4b.safetensors' -Kind image).Count -gt 0)  "family override: Klein under IMAGE kind -> override applies (unchanged)"
Assert ((Get-ModelFamilyOverride 'flux-2-klein-4b.safetensors' -Kind edit).Count -eq 0)   "family override: Klein under EDIT kind -> NO override (edit recipe untouched)"
Assert ((Get-ModelFamilyOverride 'flux-2-klein-4b.safetensors' -Kind video).Count -eq 0)  "family override: Klein under VIDEO kind -> NO override (non-image guarded)"

# --- Build-GenBody integration: selecting a Klein checkpoint rewrites the Z-Image recipe knobs ---
$bKleinD = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'flux-2-klein-4b.safetensors'
Assert ($bKleinD.model -eq 'flux-2-klein-4b.safetensors')         "Klein body: -Model swaps the checkpoint"
Assert ($bKleinD.steps -eq 4 -and $bKleinD.cfgscale -eq 1)        "Klein body: distilled overrides steps=4 / cfg=1 (over Z-Image 35/4.5)"
Assert ($bKleinD.sampler -eq 'euler' -and $bKleinD.scheduler -eq 'Flux2') "Klein body: distilled overrides sampler=euler / scheduler=Flux2 (over dpmpp_2m/karras)"
Assert (-not $bKleinD.ContainsKey('negativeprompt'))             "Klein body: drops the Z-Image curated negative (FLUX.2 carries no negative)"
$bKleinB = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'flux-2-klein-base-4b.safetensors'
Assert ($bKleinB.steps -eq 20 -and $bKleinB.cfgscale -eq 5)       "Klein body: base overrides steps=20 / cfg=5"
Assert ($bKleinB.sampler -eq 'euler' -and $bKleinB.scheduler -eq 'Flux2') "Klein body: base overrides sampler=euler / scheduler=Flux2"
# user -Negative is honoured even on Klein (set, since the recipe negative was dropped first by the override)
$bKleinNeg = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'flux-2-klein-4b.safetensors' -Negative 'extra limbs'
Assert ($bKleinNeg.negativeprompt -eq 'extra limbs')             "Klein body: explicit -Negative still applies (recipe negative dropped, user negative set)"
# NON-Klein -Model override must leave the Z-Image recipe knobs BYTE-FOR-BYTE intact (purely additive proof)
$bChroma = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'Chroma1-HD-fp8mixed-final.safetensors'
Assert ($bChroma.steps -eq 35 -and $bChroma.cfgscale -eq 4.5)    "non-Klein -Model: Z-Image knobs unchanged (steps 35 / cfg 4.5)"
Assert ($bChroma.sampler -eq 'dpmpp_2m' -and $bChroma.scheduler -eq 'karras') "non-Klein -Model: Z-Image sampler/scheduler unchanged"
Assert ($bChroma.negativeprompt -match 'worst quality')          "non-Klein -Model: Z-Image negative preserved (override is Klein-only)"
# -Kind guard in Build-GenBody: a Klein checkpoint picked under -Edit must NOT inherit the FLUX.2 image
# override — the edit recipe (Qwen-Image-Edit, steps=20 / cfg=2.5, no scheduler) stays intact, only the
# checkpoint swaps. This stops a hand-typed `doki gen -Edit -Model flux-2-klein-4b.safetensors` from
# clobbering the edit recipe's sampler knobs.
$bKleinEdit = Build-GenBody -Recipe (Get-GenRecipe -Kind edit) -PromptFields (Get-GenPromptFields -Kind edit -Idea 'x') -SessionId 's' -Model 'flux-2-klein-4b.safetensors' -Kind edit
Assert ($bKleinEdit.model -eq 'flux-2-klein-4b.safetensors')      "Klein under -Edit: -Model still swaps the checkpoint"
Assert ($bKleinEdit.steps -eq 20 -and $bKleinEdit.cfgscale -eq 2.5) "Klein under -Edit: edit recipe steps=20 / cfg=2.5 kept (NO FLUX.2 override)"
Assert (-not $bKleinEdit.ContainsKey('scheduler'))               "Klein under -Edit: no Flux2 scheduler injected (override guarded off non-image)"
Assert (-not $bKleinEdit.ContainsKey('sampler'))                 "Klein under -Edit: no euler sampler injected (edit recipe untouched)"

# --- Qwen-Image (BASE, in-image text) GGUF family override: same additive contract as FLUX.2 Klein ---
# The BASE Qwen-Image GGUF is the NON-distilled t2i unet, so it needs a REAL CFG (cfg 1 only suits the
# distilled/Lightning preset). steps=20 / cfg=4 are the SwarmUI-doc-blessed quality/speed sweet spot;
# sampler=euler + scheduler=simple are doc/template-confirmed (image_qwen_image.json KSampler). Keyed on the
# QuantStack underscore filename 'Qwen_Image-*.gguf' so it can NEVER collide with the lowercase Edit-2511
# safetensors (which routes via -Edit / Kind=edit and is guarded off anyway).
Assert ((Get-ModelFamilyOverride 'qwen_image_edit_2511_fp8mixed.safetensors').Count -eq 0) "family override: Qwen-Image-EDIT safetensors -> no override (distinct from base GGUF)"
$ovQ = Get-ModelFamilyOverride 'Qwen_Image-Q4_K_M.gguf'
Assert ($ovQ.steps -eq 20 -and $ovQ.cfgscale -eq 4)             "family override: Qwen-Image base GGUF -> 20 steps / cfg 4 (SwarmUI-doc band)"
Assert ($ovQ.sampler -eq 'euler' -and $ovQ.scheduler -eq 'simple') "family override: Qwen-Image base GGUF -> euler / simple (template-confirmed)"
Assert ((Get-ModelFamilyOverride 'QWEN_IMAGE-Q4_K_M.GGUF').sampler -eq 'euler') "family override: Qwen-Image case-insensitive filename match"
# -Kind guard (defense-in-depth): a Qwen base GGUF hand-picked under -Edit/-Video must NOT inherit the
# image override (otherwise it would clobber that kind's recipe). Image kind (default) applies it.
Assert ((Get-ModelFamilyOverride 'Qwen_Image-Q4_K_M.gguf' -Kind image).Count -gt 0)  "family override: Qwen GGUF under IMAGE kind -> override applies"
Assert ((Get-ModelFamilyOverride 'Qwen_Image-Q4_K_M.gguf' -Kind edit).Count -eq 0)   "family override: Qwen GGUF under EDIT kind -> NO override (edit recipe untouched)"
Assert ((Get-ModelFamilyOverride 'Qwen_Image-Q4_K_M.gguf' -Kind video).Count -eq 0)  "family override: Qwen GGUF under VIDEO kind -> NO override (non-image guarded)"

# --- Build-GenBody integration: selecting the Qwen-Image base GGUF rewrites the Z-Image recipe knobs ---
$bQwen = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'Qwen_Image-Q4_K_M.gguf'
Assert ($bQwen.model -eq 'Qwen_Image-Q4_K_M.gguf')              "Qwen body: -Model swaps the checkpoint"
Assert ($bQwen.steps -eq 20 -and $bQwen.cfgscale -eq 4)         "Qwen body: overrides steps=20 / cfg=4 (over Z-Image recipe)"
Assert ($bQwen.sampler -eq 'euler' -and $bQwen.scheduler -eq 'simple') "Qwen body: overrides sampler=euler / scheduler=simple"
# DESIGN CHOICE: unlike FLUX.2 Klein, Qwen-Image accepts a negative fine at CFG 4, so the Z-Image curated
# negative is KEPT (NOT dropped). This is the conservative default; the official card's empty-negative is an
# acceptable alternative but removing a working negative is not required and Klein behaviour stays untouched.
Assert ($bQwen.negativeprompt -match 'worst quality')          "Qwen body: Z-Image curated negative KEPT (Qwen runs fine with a negative at CFG 4)"

# --- Build-GenBody: user -Negative appends to (image) / sets (else) the negativeprompt ---
$bNegImg = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Negative 'extra limbs'
Assert ($bNegImg.negativeprompt -match 'worst quality' -and $bNegImg.negativeprompt -match 'extra limbs') "-Negative -> appended to the recipe negative (image)"
$bNegMus = Build-GenBody -Recipe (Get-GenRecipe -Kind music) -PromptFields (Get-GenPromptFields -Kind music -Idea 'x') -SessionId 's' -Negative 'distorted'
Assert ($bNegMus.negativeprompt -eq 'distorted') "-Negative -> sets negativeprompt when the recipe has none (music)"
$bNoNeg = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's'
Assert ($bNoNeg.negativeprompt -notmatch 'extra limbs') "no -Negative -> recipe negative unchanged"

# --- Build-GenBody: aspect-ratio presets reshape width/height (image/edit only) ---
function Test-Aspect([string]$Aspect, [int]$W, [int]$H) {
    Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Aspect $Aspect
}
$a169 = Test-Aspect '16:9'; Assert ($a169.width -eq 1344 -and $a169.height -eq 768)  "aspect 16:9 -> 1344x768 (landscape)"
$a916 = Test-Aspect '9:16'; Assert ($a916.width -eq 768  -and $a916.height -eq 1344) "aspect 9:16 -> 768x1344 (portrait)"
$a43  = Test-Aspect '4:3';  Assert ($a43.width  -eq 1152 -and $a43.height  -eq 896)  "aspect 4:3 -> 1152x896"
$a34  = Test-Aspect '3:4';  Assert ($a34.width  -eq 896  -and $a34.height  -eq 1152) "aspect 3:4 -> 896x1152"
$aDef = Test-Aspect '1:1';  Assert ($aDef.width -eq 1024 -and $aDef.height -eq 1024) "aspect 1:1 -> 1024x1024 (square)"
$aNone = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's'
Assert ($aNone.width -eq 1024 -and $aNone.height -eq 1024) "no aspect -> recipe default dims unchanged"

# --- music composer: lyrics (vs [instrumental]) + duration/bpm overrides (ACE-Step) ---
$mInst = Get-GenPromptFields -Kind music -Idea 'lofi hip hop'
Assert ($mInst.prompt -eq '[instrumental]' -and $mInst.textaudiostyle -eq 'lofi hip hop') "music (no lyrics) -> [instrumental] + style"
$mSong = Get-GenPromptFields -Kind music -Idea 'pop ballad' -Lyrics 'hello from the other side'
Assert ($mSong.prompt -eq 'hello from the other side' -and $mSong.textaudiostyle -eq 'pop ballad') "music -Lyrics -> lyrics in prompt, idea stays the style"
$mBody = Build-GenBody -Recipe (Get-GenRecipe -Kind music) -PromptFields (Get-GenPromptFields -Kind music -Idea 'x') -SessionId 's' -Duration 45 -Bpm 90
Assert ($mBody.textaudioduration -eq 45 -and $mBody.textaudiobpm -eq 90) "music -Duration/-Bpm -> override recipe defaults"
$mDef = Build-GenBody -Recipe (Get-GenRecipe -Kind music) -PromptFields (Get-GenPromptFields -Kind music -Idea 'x') -SessionId 's'
Assert ($mDef.textaudioduration -eq 10 -and $mDef.textaudiobpm -eq 128) "music (no overrides) -> recipe defaults 10s / 128bpm"
Assert ($mDef.model -eq 'acestep_v1.5_turbo.safetensors' -and $mDef.steps -eq 10 -and $mDef.cfgscale -eq 1) "music DEFAULT body -> turbo / 10 steps / cfg 1 (unchanged)"
# -Quality body: the xl_base recipe (50/6/euler/simple) flows verbatim through the generic recipe merge.
$mQBody = Build-GenBody -Recipe (Get-GenRecipe -Kind music -Quality) -PromptFields (Get-GenPromptFields -Kind music -Idea 'x') -SessionId 's'
Assert ($mQBody.model -eq 'acestep_v1.5_xl_base_bf16.safetensors' -and $mQBody.steps -eq 50 -and $mQBody.cfgscale -eq 6) "music -Quality body -> xl_base / 50 steps / cfg 6"
Assert ($mQBody.sampler -eq 'euler' -and $mQBody.scheduler -eq 'simple') "music -Quality body -> euler / simple flow through to body"
# -Quality still honours -Duration/-Bpm overrides (the music knobs are recipe-agnostic).
$mQOv = Build-GenBody -Recipe (Get-GenRecipe -Kind music -Quality) -PromptFields (Get-GenPromptFields -Kind music -Idea 'x') -SessionId 's' -Duration 60 -Bpm 100
Assert ($mQOv.textaudioduration -eq 60 -and $mQOv.textaudiobpm -eq 100) "music -Quality -> -Duration/-Bpm still override"

# --- video -Quality body: the A14B GGUF dual-expert recipe flows verbatim through the generic recipe merge ---
$vQBody = Build-GenBody -Recipe (Get-GenRecipe -Kind video -Quality) -PromptFields (Get-GenPromptFields -Kind video -Idea 'x') -SessionId 's' -Kind video
Assert ($vQBody.model -eq 'Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf') "video -Quality body -> HIGH-noise GGUF base expert"
Assert ($vQBody.refinermethod -eq 'StepSwap' -and $vQBody.refinermodel -eq 'Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf') "video -Quality body -> StepSwap + LOW-noise Refiner Model flow through to body"
Assert ($vQBody.refinercontrolpercentage -eq 0.5 -and $vQBody.cfgscale -eq 5) "video -Quality body -> control% 0.5 + cfg 5"
# the DEFAULT video body stays the 5B (no refiner keys) — proves the new arm is opt-in only
$vDefBody = Build-GenBody -Recipe (Get-GenRecipe -Kind video) -PromptFields (Get-GenPromptFields -Kind video -Idea 'x') -SessionId 's' -Kind video
Assert ($vDefBody.model -eq 'wan2.2_ti2v_5B_fp16.safetensors' -and -not $vDefBody.ContainsKey('refinermodel')) "video DEFAULT body -> Wan 2.2 5B / no refinermodel (unchanged)"

# --- doki.ps1 argv -> recipe seam: the ENTRYPOINT must declare [switch]$Quality AND forward it on -BodyOnly ---
# The blocks above prove the pure helpers (and GenCliTests proves the C# argv emits -Quality), but NOTHING
# else exercises doki.ps1 itself — the script the web host actually runs (`gen ... -BodyOnly` returns the
# GenerateText2Image body JSON it injects a session_id into). If -Quality were dropped from doki.ps1's param
# block or from its `Invoke-Gen -Quality:$Quality` forward, music would silently fall back to turbo with every
# OTHER suite still green. So shell the real doki.ps1 (child pwsh, no GPU/network — -BodyOnly stops before any
# SwarmUI call) and assert the emitted body carries the xl_base quality tier.
$dokiPs1 = Join-Path $PSScriptRoot "..\doki.ps1"
if (-not (Test-Path $dokiPs1)) { Write-Error "doki.ps1 not found at $dokiPs1"; exit 2 }
$qOut = & pwsh -NoProfile -File $dokiPs1 gen "upbeat synthwave" -Music -Quality -BodyOnly 2>&1
$qLine = @($qOut | ForEach-Object { "$_" } | Where-Object { $_ -match '^\s*\{.*\}\s*$' })[-1]   # the JSON body line
$qBody = $null; try { $qBody = $qLine | ConvertFrom-Json } catch { }
Assert ($null -ne $qBody) "doki.ps1 gen -Music -Quality -BodyOnly -> emits a parseable JSON body"
Assert ($qBody -and $qBody.model -eq 'acestep_v1.5_xl_base_bf16.safetensors') "doki.ps1 forwards -Quality -> body carries xl_base model (not turbo)"
Assert ($qBody -and $qBody.steps -eq 50 -and $qBody.cfgscale -eq 6) "doki.ps1 -Quality body -> 50 steps / cfg 6 (xl_base params reach the body)"
# control: WITHOUT -Quality the same entrypoint must stay on the turbo default (the forward is opt-in, not always-on).
$dOut = & pwsh -NoProfile -File $dokiPs1 gen "upbeat synthwave" -Music -BodyOnly 2>&1
$dLine = @($dOut | ForEach-Object { "$_" } | Where-Object { $_ -match '^\s*\{.*\}\s*$' })[-1]
$dBody = $null; try { $dBody = $dLine | ConvertFrom-Json } catch { }
Assert ($dBody -and $dBody.model -eq 'acestep_v1.5_turbo.safetensors') "doki.ps1 gen -Music (no -Quality) -> body stays on turbo default (opt-in, unchanged)"

# VIDEO mirror: -Quality must forward through doki.ps1 for kind=video too (a dropped forward would silently
# fall back to the 5B with every other suite still green). Shell the real doki.ps1 -BodyOnly (no GPU/network).
$vqOut = & pwsh -NoProfile -File $dokiPs1 gen "a koi swimming" -Video -Quality -BodyOnly 2>&1
$vqLine = @($vqOut | ForEach-Object { "$_" } | Where-Object { $_ -match '^\s*\{.*\}\s*$' })[-1]
$vqBody = $null; try { $vqBody = $vqLine | ConvertFrom-Json } catch { }
Assert ($null -ne $vqBody) "doki.ps1 gen -Video -Quality -BodyOnly -> emits a parseable JSON body"
Assert ($vqBody -and $vqBody.model -eq 'Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf') "doki.ps1 forwards -Quality for video -> body carries the A14B HIGH-noise GGUF (not the 5B)"
Assert ($vqBody -and $vqBody.refinermodel -eq 'Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf') "doki.ps1 -Video -Quality body -> Refiner Model = LOW-noise GGUF (dual-expert reaches the body)"
# control: WITHOUT -Quality the same entrypoint stays on the Wan 2.2 5B default (opt-in, unchanged)
$vdOut = & pwsh -NoProfile -File $dokiPs1 gen "a koi swimming" -Video -BodyOnly 2>&1
$vdLine = @($vdOut | ForEach-Object { "$_" } | Where-Object { $_ -match '^\s*\{.*\}\s*$' })[-1]
$vdBody = $null; try { $vdBody = $vdLine | ConvertFrom-Json } catch { }
Assert ($vdBody -and $vdBody.model -eq 'wan2.2_ti2v_5B_fp16.safetensors') "doki.ps1 gen -Video (no -Quality) -> body stays on the Wan 2.2 5B default (unchanged)"

# --- Expand-Wildcards: __name__ -> a random line from <name>.txt, seed-reproducible, unknown left as-is ---
$wcDir = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-wc-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $wcDir | Out-Null
Set-Content (Join-Path $wcDir 'color.txt') @('# a comment', 'red', 'green', 'blue', '')
$w1 = Expand-Wildcards -Text 'a __color__ car' -Seed 7 -WildcardDir $wcDir
Assert ($w1 -in @('a red car', 'a green car', 'a blue car')) "wildcard -> one of the file's lines (comments/blanks skipped)"
$w2 = Expand-Wildcards -Text 'a __color__ car' -Seed 7 -WildcardDir $wcDir
Assert ($w1 -eq $w2)                                                "same seed -> same draw (reproducible)"
Assert ((Expand-Wildcards -Text 'plain text' -Seed 1 -WildcardDir $wcDir) -eq 'plain text') "no wildcard token -> text unchanged"
Assert ((Expand-Wildcards -Text 'a __nope__ x' -Seed 1 -WildcardDir $wcDir) -eq 'a __nope__ x') "unknown wildcard -> token left literal"
Assert ((Expand-Wildcards -Text '__color__ vs __color__' -Seed 3 -WildcardDir $wcDir) -match '^(red|green|blue) vs (red|green|blue)$') "multiple tokens all expand"
Remove-Item $wcDir -Recurse -Force -ErrorAction SilentlyContinue

# --- Expand-References: @name -> references/<name>.txt snippet; unknown left as-is ---
$refDir = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-ref-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $refDir | Out-Null
Set-Content (Join-Path $refDir 'hero.txt') 'a tall knight in silver armor'
Assert ((Expand-References -Text 'paint @hero at dawn' -RefDir $refDir) -eq 'paint a tall knight in silver armor at dawn') "@ref -> expands to the saved snippet"
Assert ((Expand-References -Text 'just @unknown here' -RefDir $refDir) -eq 'just @unknown here') "@ref unknown -> left as-is"
Assert ((Expand-References -Text 'no refs at all' -RefDir $refDir) -eq 'no refs at all') "no @ -> unchanged"
Remove-Item $refDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`ndoki-gen: $script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
exit $(if ($script:fail) { 1 } else { 0 })
