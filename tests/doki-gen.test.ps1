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
Assert ((Resolve-GenKind -InfiniteTalk) -eq 'infinitetalk') "-InfiniteTalk -> infinitetalk (audio-driven talking-video)"
$ambiguous = $false
try { Resolve-GenKind -Video -Music | Out-Null } catch { $ambiguous = $true }
Assert $ambiguous                             "-Video -Music -> throws (ambiguous)"
$ambiguous2 = $false
try { Resolve-GenKind -FaceId -Foley | Out-Null } catch { $ambiguous2 = $true }
Assert $ambiguous2                            "-FaceId -Foley -> throws (ambiguous)"
$ambiguous3 = $false
try { Resolve-GenKind -InfiniteTalk -I2v | Out-Null } catch { $ambiguous3 = $true }
Assert $ambiguous3                            "-InfiniteTalk -I2v -> throws (ambiguous)"

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

# infinitetalk = the InfiniteTalk custom-workflow alias (audio-driven talking-video on Wan2.1-I2V-14B). Maps to
# comfyuicustomworkflow=InfiniteTalk; the portrait rides the init-image channel and the voice rides -Audio.
$itk = Get-GenRecipe -Kind infinitetalk
Assert ($itk.comfyuicustomworkflow -eq 'InfiniteTalk') "infinitetalk -> InfiniteTalk custom workflow (the talking-video alias)"

# --- Invoke-Gen: -InfiniteTalk REQUIRES BOTH -InitImage <portrait> AND -Audio <clip> up front (mirrors the
# -Edit/-FaceId init-image guard, plus a NEW audio guard). Both must fire loudly BEFORE any SwarmUI contact, so
# the throw paths return before the probe; the positive path uses real temp files + -BodyOnly (no /API call).
$itNoInit = $false; $itErr = $null
try { Invoke-Gen -Prompt 'a person speaking' -Kind infinitetalk -Audio 'x.wav' | Out-Null } catch { $itNoInit = $true; $itErr = "$($_.Exception.Message)" }
Assert $itNoInit                                          "-InfiniteTalk with NO -InitImage -> throws (a portrait is mandatory)"
Assert ($itErr -match 'requires\s+-InitImage')            "-InfiniteTalk missing -InitImage -> the error names 'requires -InitImage'"
$itPortrait = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-it-img-$([guid]::NewGuid().ToString('N')).png"
Set-Content -LiteralPath $itPortrait -Value 'not-a-real-image-just-bytes' -NoNewline
$itNoAudio = $false; $itErr2 = $null
try { Invoke-Gen -Prompt 'a person speaking' -Kind infinitetalk -InitImage $itPortrait | Out-Null } catch { $itNoAudio = $true; $itErr2 = "$($_.Exception.Message)" }
Assert $itNoAudio                                         "-InfiniteTalk with portrait but NO -Audio -> throws (the driving voice is mandatory)"
Assert ($itErr2 -match 'requires\s+-Audio')               "-InfiniteTalk missing -Audio -> the error names 'requires -Audio' (the NEW audio guard)"
$itAudio = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-it-aud-$([guid]::NewGuid().ToString('N')).wav"
Set-Content -LiteralPath $itAudio -Value 'not-a-real-wav-just-bytes' -NoNewline
$itOk = $true
try { $ib = Invoke-Gen -Prompt 'a person speaking' -Kind infinitetalk -InitImage $itPortrait -Audio $itAudio -BodyOnly } catch { $itOk = $false }
Assert $itOk                                              "-InfiniteTalk WITH -InitImage + -Audio -> does NOT throw (both inputs supplied)"
$ibBody = $null; try { $ibBody = $ib | ConvertFrom-Json } catch {}
Assert ($ibBody -and $ibBody.comfyuicustomworkflow -eq 'InfiniteTalk') "-InfiniteTalk -BodyOnly -> body carries comfyuicustomworkflow=InfiniteTalk"
Assert ($ibBody -and $ibBody.initimage)                   "-InfiniteTalk -BodyOnly -> body carries the portrait (init image)"
Assert ($ibBody -and $ibBody.inputaudio)                  "-InfiniteTalk -BodyOnly -> body carries the driving audio (provisional inputaudio key; pinned on-GPU)"
Remove-Item -LiteralPath $itPortrait, $itAudio -Force -ErrorAction SilentlyContinue

# --- Invoke-Gen: -Audio is gated to the infinitetalk kind (mirrors the -MaskImage/-Edit kind-guard). Without
# this, Build-GenBody would inject the provisional `inputaudio` key into ANY body whenever -Audio is given, so
# `doki gen -Video -Audio x.wav` would silently smuggle a stray audio key into a Wan video body. The guard must
# fire loudly BEFORE any SwarmUI contact (same posture as -MaskImage/-Upscale), and must NOT block the one kind
# that legitimately consumes audio (infinitetalk), which is already proven to reach -BodyOnly above.
$audWrongKind = $false; $audErr = $null
try { Invoke-Gen -Prompt 'a koi swimming' -Kind video -Audio 'x.wav' | Out-Null } catch { $audWrongKind = $true; $audErr = "$($_.Exception.Message)" }
Assert $audWrongKind                                      "-Video -Audio -> throws (audio only applies to -InfiniteTalk, not a Wan video body)"
Assert ($audErr -match '(?i)-Audio only applies to .*-InfiniteTalk') "-Video -Audio -> the error names '-Audio only applies to -InfiniteTalk' (clear, up-front)"
Assert ($audErr -match '(?i)got\s+video')                 "-Video -Audio -> the error reports the offending kind (got video)"
# the audio guard must NOT block infinitetalk (its own -InitImage/-Audio guards already passed -BodyOnly above);
# re-prove the happy path returns a body, i.e. the new kind-guard permits the one audio-consuming kind.
$itPortrait2 = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-it-img2-$([guid]::NewGuid().ToString('N')).png"
$itAudio2    = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-it-aud2-$([guid]::NewGuid().ToString('N')).wav"
Set-Content -LiteralPath $itPortrait2 -Value 'not-a-real-image-just-bytes' -NoNewline
Set-Content -LiteralPath $itAudio2    -Value 'not-a-real-wav-just-bytes'   -NoNewline
$audRightKind = $true
try { Invoke-Gen -Prompt 'a person speaking' -Kind infinitetalk -InitImage $itPortrait2 -Audio $itAudio2 -BodyOnly | Out-Null } catch { $audRightKind = $false }
Assert $audRightKind                                      "-InfiniteTalk -InitImage -Audio -> does NOT throw the audio kind-guard (infinitetalk legitimately consumes audio)"
Remove-Item -LiteralPath $itPortrait2, $itAudio2 -Force -ErrorAction SilentlyContinue

# --- Resolve-GenKind: -Speak -> the GATED TTS-Audio-Suite speech kind ('speech'), with the ambiguity guard ---
Assert ((Resolve-GenKind -Speak) -eq 'speech') "-Speak -> speech (the gated TTS-Audio-Suite alternative speech path)"
$ambiguous4 = $false
try { Resolve-GenKind -Speak -Music | Out-Null } catch { $ambiguous4 = $true }
Assert $ambiguous4                             "-Speak -Music -> throws (ambiguous)"

# --- speech recipe: -Engine selects the per-engine TtsSuite-<engine> custom workflow (default IndexTTS2). The
# engine is normalized (non-alphanumerics stripped AND case-folded to a canonical token) so
# 'IndexTTS-2'/'IndexTTS2'/'indextts2'/'index tts 2' all collapse to the SAME workflow name. This routes EXACTLY
# like foley/infinitetalk (a comfyuicustomworkflow alias), so the engine is picked by WHICH workflow JSON is
# installed — no engine param ever enters the :8004 Tts.cs/api-speak path.
$spDefault = Get-GenRecipe -Kind speech
Assert ($spDefault.comfyuicustomworkflow -eq 'TtsSuite-IndexTTS2') "speech (no -Engine) -> TtsSuite-IndexTTS2 (default engine = duration/emotion control)"
Assert ($spDefault.seed -eq -1)                                    "speech -> seed -1 (like foley)"
Assert ((Get-GenRecipe -Kind speech -Engine 'Higgs').comfyuicustomworkflow -eq 'TtsSuite-Higgs') "speech -Engine Higgs -> TtsSuite-Higgs"
Assert ((Get-GenRecipe -Kind speech -Engine 'RVC').comfyuicustomworkflow -eq 'TtsSuite-RVC')     "speech -Engine RVC -> TtsSuite-RVC (voice-conversion)"
# case-fold + strip: every casing/punctuation variant of the default engine collapses to ONE canonical workflow
# name (no more 'indextts2' vs 'IndexTTS2' split-brain that would point at a different, non-existent JSON). Use
# case-SENSITIVE -ceq here: PS -eq is case-insensitive, so it would mask a casing drift in the resolved filename
# (the very bug being fixed). -ceq pins the EXACT canonical casing the on-disk TtsSuite-IndexTTS2.json must match.
Assert ((Get-GenRecipe -Kind speech -Engine 'IndexTTS-2').comfyuicustomworkflow -ceq 'TtsSuite-IndexTTS2')  "speech -Engine 'IndexTTS-2' -> canonical TtsSuite-IndexTTS2 (hyphen stripped)"
Assert ((Get-GenRecipe -Kind speech -Engine 'indextts2').comfyuicustomworkflow -ceq 'TtsSuite-IndexTTS2')   "speech -Engine 'indextts2' -> canonical TtsSuite-IndexTTS2 (case-folded, NOT a distinct lowercase name)"
Assert ((Get-GenRecipe -Kind speech -Engine 'INDEXTTS2').comfyuicustomworkflow -ceq 'TtsSuite-IndexTTS2')   "speech -Engine 'INDEXTTS2' -> canonical TtsSuite-IndexTTS2 (upper-case folded to canonical)"
Assert ((Get-GenRecipe -Kind speech -Engine 'index tts 2').comfyuicustomworkflow -ceq 'TtsSuite-IndexTTS2') "speech -Engine 'index tts 2' -> canonical TtsSuite-IndexTTS2 (spaces stripped + case-folded)"

# --- Get-GenPromptFields: speech idea is the LITERAL spoken text -> ${prompt} verbatim, NOT the <mpprompt:..>
# cinematic rewriter (which would rewrite the words being spoken). No SwarmUI tags either (image-family only).
$pfSpeech = Get-GenPromptFields -Kind speech -Idea 'Hello there, this is a test.'
Assert ($pfSpeech.prompt -eq 'Hello there, this is a test.')       "speech idea -> literal text (no <mpprompt:..> rewriter wrap)"
Assert (-not ($pfSpeech.prompt -match '<mpprompt:|<lora:|<segment:')) "speech -> no rewriter wrap / no SwarmUI tags (literal spoken text)"

# --- Invoke-Gen: -Speak REQUIRES text to synthesize (mirrors the -Edit/-FaceId up-front guards); a reference
# voice clip is OPTIONAL (rides -Audio). The throw path returns before any SwarmUI contact; the happy path uses
# -BodyOnly (no /API call) and proves the body carries the workflow alias + the literal text in prompt.
$speakNoText = $false; $speakErr = $null
try { Invoke-Gen -Prompt '   ' -Kind speech | Out-Null } catch { $speakNoText = $true; $speakErr = "$($_.Exception.Message)" }
Assert $speakNoText                                        "-Speak with blank text -> throws (nothing to synthesize)"
Assert ($speakErr -match '(?i)-Speak requires text')      "-Speak missing text -> the error names '-Speak requires text' (clear, up-front)"
$spOk = $true; $spBody = $null
try { $spBody = Invoke-Gen -Prompt 'Read this aloud.' -Kind speech -Engine 'IndexTTS2' -BodyOnly } catch { $spOk = $false }
Assert $spOk                                              "-Speak WITH text -> does NOT throw (text supplied)"
$spBodyObj = $null; try { $spBodyObj = $spBody | ConvertFrom-Json } catch {}
Assert ($spBodyObj -and $spBodyObj.comfyuicustomworkflow -eq 'TtsSuite-IndexTTS2') "-Speak -BodyOnly -> body carries comfyuicustomworkflow=TtsSuite-IndexTTS2"
Assert ($spBodyObj -and $spBodyObj.prompt -eq 'Read this aloud.') "-Speak -BodyOnly -> the literal spoken text rides \${prompt}"
Assert ($spBodyObj -and -not $spBodyObj.inputaudio)       "-Speak with no -Audio -> no audio body-key (ref clip is optional)"

# --- speech accepts an OPTIONAL reference voice clip via -Audio (zero-shot clone). The -Audio kind-guard permits
# it (it permits infinitetalk AND speech); the clip rides the same provisional inputaudio body-key InfiniteTalk
# parks (pinned on-GPU once the authored workflow names its audio-load node).
$spRef = Join-Path ([System.IO.Path]::GetTempPath()) "dokidex-speak-ref-$([guid]::NewGuid().ToString('N')).wav"
Set-Content -LiteralPath $spRef -Value 'not-a-real-wav-just-bytes' -NoNewline
$spRefOk = $true; $spRefBody = $null
try { $spRefBody = Invoke-Gen -Prompt 'Clone my voice.' -Kind speech -Engine 'IndexTTS2' -Audio $spRef -BodyOnly } catch { $spRefOk = $false }
Assert $spRefOk                                           "-Speak -Audio <ref> -> does NOT throw the audio kind-guard (speech legitimately consumes a reference clip)"
$spRefObj = $null; try { $spRefObj = $spRefBody | ConvertFrom-Json } catch {}
Assert ($spRefObj -and $spRefObj.inputaudio)             "-Speak -Audio -BodyOnly -> body carries the reference voice (provisional inputaudio key)"
Remove-Item -LiteralPath $spRef -Force -ErrorAction SilentlyContinue

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

# --- Nunchaku NVFP4 variants (gated -Nunchaku speed lever): the family override covers them with ZERO/ONE change ---
# (1) FLUX.2 Klein NVFP4 (flux-2-klein-4b-nvfp4.safetensors, BFL's OWN NATIVE NVFP4 checkpoint — NOT a nunchaku
#     svdq quant; it loads via ComfyUI's native FLUX.2 FP4 path, so it ships in -Models full, not the -Nunchaku
#     block) — the existing 'flux-2-klein*' glob ALREADY matches it. The filename has no '-base-' infix so the glob
#     takes the DISTILLED branch (4 steps / cfg 1 / euler / Flux2). CAVEAT: BFL's nvfp4 card states NO inference
#     config and does NOT label the file distilled-vs-base (the '-base-' convention was Comfy-Org's repackage
#     naming, not BFL's nvfp4 repo), so this distilled routing is the CONSERVATIVE assumption and is on-GPU-
#     unverified (a 4-vs-base-step A/B) — see the matching note in doki-gen.ps1. Cleanest integration: no recipe change.
$ovKleinNvfp4 = Get-ModelFamilyOverride 'flux-2-klein-4b-nvfp4.safetensors'
Assert ($ovKleinNvfp4.steps -eq 4 -and $ovKleinNvfp4.cfgscale -eq 1) "Nunchaku: Klein NVFP4 -> inherits the (conservative, on-GPU-unverified) DISTILLED branch (4 steps / cfg 1) via the existing flux-2-klein* glob (no recipe change)"
Assert ($ovKleinNvfp4.sampler -eq 'euler' -and $ovKleinNvfp4.scheduler -eq 'Flux2') "Nunchaku: Klein NVFP4 -> euler / Flux2 (shares the distilled Klein sampler knobs)"
# the NVFP4 file must NOT be misread as the base/quality variant (it has no '-base-' infix, mirroring the distilled vs base convention)
Assert ($ovKleinNvfp4.steps -ne 20) "Nunchaku: Klein NVFP4 -> NOT the 20-step base branch (no '-base-' infix -> distilled)"
# Build-GenBody integration: selecting the Klein NVFP4 checkpoint also drops the Z-Image negative (the
# 'flux-2-klein*' negative-drop guard already fires on the nvfp4 name — same byte-for-byte path as plain Klein).
$bKleinNvfp4 = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'flux-2-klein-4b-nvfp4.safetensors'
Assert ($bKleinNvfp4.model -eq 'flux-2-klein-4b-nvfp4.safetensors') "Nunchaku Klein NVFP4 body: -Model swaps to the nvfp4 checkpoint"
Assert ($bKleinNvfp4.steps -eq 4 -and $bKleinNvfp4.sampler -eq 'euler') "Nunchaku Klein NVFP4 body: distilled knobs applied (4 steps / euler)"
Assert (-not $bKleinNvfp4.ContainsKey('negativeprompt')) "Nunchaku Klein NVFP4 body: Z-Image negative dropped (the flux-2-klein* guard fires on the nvfp4 name too)"

# (2) Qwen-Image NVFP4 base (svdq-fp4_r128-qwen-image.safetensors) — the .gguf-locked Qwen match does NOT catch
#     the .safetensors NVFP4 file, so ONE additive line keys it to the SAME base band (20 steps / cfg 4 / euler /
#     simple). It is the NON-distilled base, so it needs a REAL cfg (cfg 1 would only suit a Lightning distill).
Assert ((Get-ModelFamilyOverride 'svdq-fp4_r128-qwen-image.safetensors').steps -eq 20) "Nunchaku: Qwen NVFP4 base -> 20 steps (additive svdq-*qwen-image override, same base band as the GGUF)"
$ovQwenNvfp4 = Get-ModelFamilyOverride 'svdq-fp4_r128-qwen-image.safetensors'
Assert ($ovQwenNvfp4.cfgscale -eq 4) "Nunchaku: Qwen NVFP4 base -> cfg 4 (real cfg; the non-distilled base, not a Lightning distill)"
Assert ($ovQwenNvfp4.sampler -eq 'euler' -and $ovQwenNvfp4.scheduler -eq 'simple') "Nunchaku: Qwen NVFP4 base -> euler / simple (shares the base Qwen sampler knobs)"
# the int4 (pre-Blackwell) build shares the base band; case-insensitive match holds
Assert ((Get-ModelFamilyOverride 'svdq-int4_r128-qwen-image.safetensors').steps -eq 20) "Nunchaku: Qwen NVFP4/INT4 svdq variants both match the base band (svdq-*qwen-image)"
Assert ((Get-ModelFamilyOverride 'SVDQ-FP4_R128-QWEN-IMAGE.SAFETENSORS').sampler -eq 'euler') "Nunchaku: Qwen svdq override is case-insensitive"
# the Lightning fp4 distills (4/8-step) are EXCLUDED — they want cfg=1 / low steps, not this base band
Assert ((Get-ModelFamilyOverride 'svdq-fp4_r128-qwen-image-lightningv2.0-4steps.safetensors').Count -eq 0) "Nunchaku: Qwen NVFP4 LIGHTNING distill -> NO base override (excluded; it wants cfg=1/low-step, fetched only on demand)"
# -Kind guard (defense-in-depth): the svdq override is image-only, like the GGUF/Klein overrides
Assert ((Get-ModelFamilyOverride 'svdq-fp4_r128-qwen-image.safetensors' -Kind image).Count -gt 0) "Nunchaku: Qwen NVFP4 under IMAGE kind -> override applies"
Assert ((Get-ModelFamilyOverride 'svdq-fp4_r128-qwen-image.safetensors' -Kind edit).Count -eq 0)  "Nunchaku: Qwen NVFP4 under EDIT kind -> NO override (non-image guarded)"
# Build-GenBody integration: the Qwen NVFP4 base body carries the base band over the Z-Image recipe
$bQwenNvfp4 = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'svdq-fp4_r128-qwen-image.safetensors'
Assert ($bQwenNvfp4.model -eq 'svdq-fp4_r128-qwen-image.safetensors') "Nunchaku Qwen NVFP4 body: -Model swaps to the svdq checkpoint"
Assert ($bQwenNvfp4.steps -eq 20 -and $bQwenNvfp4.cfgscale -eq 4) "Nunchaku Qwen NVFP4 body: overrides steps=20 / cfg=4 (over the Z-Image recipe)"
Assert ($bQwenNvfp4.negativeprompt -match 'worst quality') "Nunchaku Qwen NVFP4 body: Z-Image curated negative KEPT (same as the Qwen GGUF; runs fine with a negative at cfg 4)"
# (3) Z-Image-Turbo NVFP4 (svdq-fp4_r128-z-image-turbo.safetensors, nunchaku-ai/nunchaku-z-image-turbo) — the
#     nunchaku Z-Image-Turbo 4-bit weight (added nunchaku v1.1.0, perf-boosted v1.2.0). This accelerates DokiDex's
#     #1 photoreal + real-time-canvas BASE (Z-Image-Turbo) on Blackwell, so it needs the EXISTING Turbo recipe
#     (steps 8 / cfg 1 / euler / simple — the -Fast image band), NOT the Z-Image BASE 35/4.5 default. An additive
#     svdq-*z-image-turbo branch keys it to the Turbo knobs. It must NOT inherit the bf16 BASE recipe.
$ovZTurboNvfp4 = Get-ModelFamilyOverride 'svdq-fp4_r128-z-image-turbo.safetensors'
Assert ($ovZTurboNvfp4.steps -eq 8 -and $ovZTurboNvfp4.cfgscale -eq 1) "Nunchaku: Z-Image-Turbo NVFP4 -> the Turbo band (8 steps / cfg 1), not the Z-Image BASE 35/4.5"
Assert ($ovZTurboNvfp4.sampler -eq 'euler' -and $ovZTurboNvfp4.scheduler -eq 'simple') "Nunchaku: Z-Image-Turbo NVFP4 -> euler / simple (the existing Turbo sampler knobs)"
# the int4 (pre-Blackwell) + r32/r256 rank variants share the same Turbo band; case-insensitive match holds
Assert ((Get-ModelFamilyOverride 'svdq-int4_r128-z-image-turbo.safetensors').steps -eq 8) "Nunchaku: Z-Image-Turbo svdq INT4/rank variants all match the Turbo band (svdq-*z-image-turbo)"
Assert ((Get-ModelFamilyOverride 'SVDQ-FP4_R128-Z-IMAGE-TURBO.SAFETENSORS').sampler -eq 'euler') "Nunchaku: Z-Image-Turbo svdq override is case-insensitive"
# -Kind guard (defense-in-depth): the svdq z-image-turbo override is image-only, like the GGUF/Klein/Qwen overrides
Assert ((Get-ModelFamilyOverride 'svdq-fp4_r128-z-image-turbo.safetensors' -Kind image).Count -gt 0) "Nunchaku: Z-Image-Turbo NVFP4 under IMAGE kind -> override applies"
Assert ((Get-ModelFamilyOverride 'svdq-fp4_r128-z-image-turbo.safetensors' -Kind video).Count -eq 0)  "Nunchaku: Z-Image-Turbo NVFP4 under VIDEO kind -> NO override (non-image guarded)"
# Build-GenBody integration: the Z-Image-Turbo NVFP4 body carries the Turbo band over the Z-Image BASE recipe.
# Z-Image Turbo carries NO curated negative (the -Fast recipe omits it), so the override DROPS the BASE negative.
$bZTurboNvfp4 = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'svdq-fp4_r128-z-image-turbo.safetensors'
Assert ($bZTurboNvfp4.model -eq 'svdq-fp4_r128-z-image-turbo.safetensors') "Nunchaku Z-Image-Turbo NVFP4 body: -Model swaps to the svdq z-image-turbo checkpoint"
Assert ($bZTurboNvfp4.steps -eq 8 -and $bZTurboNvfp4.cfgscale -eq 1) "Nunchaku Z-Image-Turbo NVFP4 body: overrides steps=8 / cfg=1 (Turbo band over the Z-Image BASE 35/4.5)"
Assert ($bZTurboNvfp4.sampler -eq 'euler' -and $bZTurboNvfp4.scheduler -eq 'simple') "Nunchaku Z-Image-Turbo NVFP4 body: euler / simple (over the BASE dpmpp_2m/karras)"
Assert (-not $bZTurboNvfp4.ContainsKey('negativeprompt')) "Nunchaku Z-Image-Turbo NVFP4 body: Z-Image BASE curated negative dropped (Turbo carries no negative)"
# CRITICAL additive guard: the svdq z-image-turbo branch is keyed on the '-turbo' suffix, so the plain Z-Image
# BASE bf16 default (z_image_bf16.safetensors) must STILL get NO override — its 35/4.5 path is byte-for-byte intact.
Assert ((Get-ModelFamilyOverride 'z_image_bf16.safetensors').Count -eq 0) "Nunchaku: the svdq z-image-turbo add does NOT leak onto the plain Z-Image BASE bf16 default (still no override)"

# guard: a non-svdq, non-Klein -Model still leaves the Z-Image recipe byte-for-byte (the adds are additive only)
$bZimgGuard = Build-GenBody -Recipe (Get-GenRecipe -Kind image) -PromptFields (Get-GenPromptFields -Kind image -Idea 'x' -Raw) -SessionId 's' -Model 'z_image_bf16.safetensors'
Assert ($bZimgGuard.steps -eq 35 -and $bZimgGuard.sampler -eq 'dpmpp_2m') "Nunchaku adds are additive: Z-Image -Model still keeps 35 steps / dpmpp_2m (no svdq/Klein nvfp4 leakage)"

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

# SPEECH mirror: -Speak + -Engine must forward through doki.ps1 to the speech recipe (a dropped param/forward
# would mean `doki gen -Speak` silently falls back to an image gen with every other suite still green). Shell
# the real doki.ps1 -BodyOnly (no GPU/network) and assert the emitted body carries the per-engine TtsSuite
# workflow + the literal spoken text in ${prompt}. This is the GATED TTS-Audio-Suite alternative path; it NEVER
# touches the :8004 Chatterbox default (a different transport), so there is nothing to regress on that side.
$spOut = & pwsh -NoProfile -File $dokiPs1 gen "Hello from DokiDex." -Speak -Engine "Higgs" -BodyOnly 2>&1
$spLine = @($spOut | ForEach-Object { "$_" } | Where-Object { $_ -match '^\s*\{.*\}\s*$' })[-1]
$spSeam = $null; try { $spSeam = $spLine | ConvertFrom-Json } catch { }
Assert ($null -ne $spSeam) "doki.ps1 gen -Speak -Engine Higgs -BodyOnly -> emits a parseable JSON body"
Assert ($spSeam -and $spSeam.comfyuicustomworkflow -eq 'TtsSuite-Higgs') "doki.ps1 forwards -Speak/-Engine -> body carries comfyuicustomworkflow=TtsSuite-Higgs"
Assert ($spSeam -and $spSeam.prompt -eq 'Hello from DokiDex.') "doki.ps1 -Speak body -> the literal spoken text rides \${prompt} (no <mpprompt:..> rewrite)"
# control: -Speak with no -Engine -> the default IndexTTS2 workflow (the forward defaults, not drops, the engine)
$spdOut = & pwsh -NoProfile -File $dokiPs1 gen "Default engine please." -Speak -BodyOnly 2>&1
$spdLine = @($spdOut | ForEach-Object { "$_" } | Where-Object { $_ -match '^\s*\{.*\}\s*$' })[-1]
$spdSeam = $null; try { $spdSeam = $spdLine | ConvertFrom-Json } catch { }
Assert ($spdSeam -and $spdSeam.comfyuicustomworkflow -eq 'TtsSuite-IndexTTS2') "doki.ps1 gen -Speak (no -Engine) -> defaults to TtsSuite-IndexTTS2 (engine defaulted, not dropped)"

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
