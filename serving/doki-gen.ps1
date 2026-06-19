# serving/doki-gen.ps1 — `doki gen "<idea>"` text->media.
#
# Split into PURE helpers (Resolve-GenKind / Get-GenRecipe / Get-GenPromptFields / Build-GenBody) that carry
# the docs/wiki/11-media-recipes.md table 1:1 and are exercised with NO GPU by tests/doki-gen.test.ps1, plus the
# live Invoke-Gen that POSTs to SwarmUI :7801 (needs `doki up media`). Dot-sourced by doki.ps1's `gen` verb.
#
# No top-level statements / Set-StrictMode here on purpose: dot-sourcing runs in the caller's scope, so this
# file must only DEFINE functions — never mutate the host script's strict mode or run anything on load.

# switches -> exactly one kind, with an ambiguity guard (so `gen -Video -Music` fails loudly, not silently).
function Resolve-GenKind {
    param([switch]$Video, [switch]$Music, [switch]$Edit, [switch]$I2v, [switch]$Foley, [switch]$FaceId, [switch]$Pulid, [switch]$InfiniteTalk, [switch]$LatentSync, [switch]$Speak)
    $picked = @()
    if ($Video) { $picked += 'video' }
    if ($Music) { $picked += 'music' }
    if ($Edit)  { $picked += 'edit'  }
    if ($I2v)   { $picked += 'i2v'   }
    if ($Foley) { $picked += 'foley' }
    if ($FaceId) { $picked += 'faceid' }
    if ($Pulid) { $picked += 'pulid' }
    if ($InfiniteTalk) { $picked += 'infinitetalk' }
    # -LatentSync = the GATED LIGHT lip-sync (ByteDance LatentSync, ~9.5GB / 8GB VRAM). Unlike InfiniteTalk
    # (portrait->talking-video on the ~82GB Wan2.1-14B base), this RE-SYNCS an existing clip's mouth to new audio
    # (video-in), so it requires -Audio only (no portrait -InitImage) — see Get-GenRecipe + the -Audio guard.
    if ($LatentSync) { $picked += 'latentsync' }
    # -Speak = the GATED TTS-Audio-Suite alternative speech path (15 engines + RVC), run as a ComfyUI custom
    # workflow in the GPU-EXCLUSIVE media group. This does NOT touch the coexisting-with-chat :8004 Chatterbox
    # default (Tts.cs / api-speak), which stays the unconditional everyday readback path — it's a different
    # transport (HTTP server in the LLM group). -Speak is opt-in only and requires `doki up media` + the on-GPU
    # per-engine TtsSuite-<engine> workflow (see docs/decisions.md).
    if ($Speak) { $picked += 'speech' }
    if ($picked.Count -gt 1) { throw "pick ONE of -Video / -Music / -Edit / -I2v / -Foley / -FaceId / -Pulid / -InfiniteTalk / -LatentSync / -Speak (got: $($picked -join ', '))" }
    if ($picked.Count -eq 1) { return $picked[0] }
    return 'image'
}

# Dynamic-prompt wildcards: replace each __name__ token with a random line drawn from
# media-assets/wildcards/<name>.txt (# comments + blank lines ignored), BEFORE the prompt is built — so the
# RESOLVED prompt is what generates AND what the sidecar records (reproducible: tie the draw to the gen seed;
# unknown/empty wildcards are left as the literal token). Single-pass (no nested expansion -> no infinite loop).
function Expand-Wildcards {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [int]$Seed = -1, [string]$WildcardDir)
    if (-not $Text -or $Text -notmatch '__[A-Za-z0-9_]+__') { return $Text }
    if (-not $WildcardDir) { $WildcardDir = Join-Path (Split-Path $PSScriptRoot) 'media-assets/wildcards' }
    $rng = $(if ($Seed -ge 0) { [System.Random]::new($Seed) } else { [System.Random]::new() })
    $sb = [System.Text.StringBuilder]::new()
    $last = 0
    foreach ($m in [regex]::Matches($Text, '__([A-Za-z0-9_]+)__')) {
        [void]$sb.Append($Text.Substring($last, $m.Index - $last))
        $file = Join-Path $WildcardDir "$($m.Groups[1].Value).txt"
        $rep = $m.Value   # default: leave the token if the wildcard is unknown/empty
        if (Test-Path -LiteralPath $file) {
            $lines = @(Get-Content -LiteralPath $file | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
            if ($lines.Count -gt 0) { $rep = $lines[$rng.Next($lines.Count)] }
        }
        [void]$sb.Append($rep)
        $last = $m.Index + $m.Length
    }
    [void]$sb.Append($Text.Substring($last))
    return $sb.ToString()
}

# Named @-references: replace each @name token with the saved snippet from references/<name>.txt — reusable
# prompt building blocks (e.g. @hero -> "a tall knight in silver armor, scar over the left eye") for character/
# style consistency. Expanded BEFORE wildcards (so a ref can itself contain __wildcards__). Single-pass;
# unknown @names are left as-is. The web manages the files; the recipe expands them (single source of truth).
function Expand-References {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [string]$RefDir)
    if (-not $Text -or $Text -notmatch '@[A-Za-z0-9_-]+') { return $Text }
    if (-not $RefDir) { $RefDir = Join-Path (Split-Path $PSScriptRoot) 'references' }
    $sb = [System.Text.StringBuilder]::new()
    $last = 0
    foreach ($m in [regex]::Matches($Text, '@([A-Za-z0-9_-]+)')) {
        [void]$sb.Append($Text.Substring($last, $m.Index - $last))
        $file = Join-Path $RefDir "$($m.Groups[1].Value).txt"
        $rep = $m.Value   # default: leave @name if the reference is unknown
        if (Test-Path -LiteralPath $file) { $rep = (Get-Content -LiteralPath $file -Raw).Trim() }
        [void]$sb.Append($rep)
        $last = $m.Index + $m.Length
    }
    [void]$sb.Append($Text.Substring($last))
    return $sb.ToString()
}

# Normalize a -Engine string to the CANONICAL TtsSuite workflow token (the on-disk TtsSuite-<token>.json stem).
# Two steps: (1) strip every non-alphanumeric (so 'IndexTTS-2'/'index tts 2' lose their hyphen/spaces); (2) map
# the stripped-LOWERCASE form through a known-engine table to the ONE canonical casing. Without the case-fold,
# 'indextts2' would route to TtsSuite-indextts2 while the default routes to TtsSuite-IndexTTS2 — two different
# (and only one authored) filenames. The table is the suite's 15 engines; an UNKNOWN engine falls back to its
# stripped (as-typed-case) token, so any not-yet-tabled engine still gets a TtsSuite-<engine> name. Canonical
# casings match the suite's own engine labels so the on-GPU-authored JSON can be named to match 1:1.
function Resolve-TtsEngine {
    param([Parameter(Mandatory)][string]$Engine)
    $stripped = $Engine -replace '[^A-Za-z0-9]', ''
    $canon = @{
        indextts2  = 'IndexTTS2'; higgs = 'Higgs'; rvc = 'RVC'; chatterbox = 'ChatterBox'
        f5         = 'F5'; vibevoice = 'VibeVoice'; cosyvoice3 = 'CosyVoice3'; qwen3tts = 'Qwen3TTS'
        stepaudio  = 'StepAudio'; moss = 'MOSS'; granite = 'Granite'; echo = 'Echo'; dots = 'Dots'
        kokoro     = 'Kokoro'; orpheus = 'Orpheus'
    }
    $key = $stripped.ToLowerInvariant()
    if ($canon.ContainsKey($key)) { return $canon[$key] }
    return $stripped
}

# kind (+ -Fast / -Upscale modifiers) -> the SwarmUI body fields for that recipe (model + sampler knobs),
# verbatim from docs/wiki/11-media-recipes.md. No prompt, no session — Build-GenBody merges those in later.
function Get-GenRecipe {
    param(
        [ValidateSet('image', 'video', 'music', 'edit', 'i2v', 'foley', 'faceid', 'pulid', 'infinitetalk', 'latentsync', 'speech')][string]$Kind = 'image',
        [switch]$Fast, [switch]$Upscale, [switch]$Refine, [switch]$Quality, [string]$Upscaler, [string]$Engine
    )
    $r = switch ($Kind) {
        # DEFAULT image = Z-Image BASE (non-distilled) — the quality ceiling: a real CFG + working negative
        # prompt + ~35 steps recover the micro-detail Turbo's distillation throws away. -Fast swaps to Z-Image
        # Turbo (8 steps, CFG 1) for seconds-fast drafts. Both reuse the same qwen_3_4b TE + Flux ae VAE.
        'image' {
            if ($Fast) { @{ model = 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors'; steps = 8;  cfgscale = 1;   width = 1024; height = 1024; sampler = 'euler';    scheduler = 'simple' } }
            else       { @{ model = 'z_image_bf16.safetensors';                  steps = 35; cfgscale = 4.5; width = 1024; height = 1024; sampler = 'dpmpp_2m'; scheduler = 'karras'; negativeprompt = 'blurry, lowres, deformed, extra fingers, jpeg artifacts, worst quality, low quality' } }
        }
        # Wan 2.2 5B is native 720p/24fps; sigmashift 8 + uni_pc/simple are its tuned flow settings (the
        # config the missing sigma-shift left off). -Fast picks the distilled LTXV near-real-time model.
        # -Quality = the GATED Wan 2.2 A14B GGUF dual-expert quality tier (the 5B default stays byte-for-byte;
        # the quality arm is opt-in only). SwarmUI has NO auto-pairing for the two noise experts — it reuses
        # its image-refiner StepSwap as a noise-level step-swap: base = HIGH-noise expert, Refiner Model =
        # LOW-noise expert, RefinerMethod=StepSwap, RefinerControlPercentage=0.5 (the high expert is retuned to
        # run only the first ~50% of steps, then hands off to the low expert). This dual-expert WIRING is
        # AUTHORITATIVELY doc-sourced from SwarmUI's docs/Video Model Support.md; cfg=5 is its T2V 14B doc
        # reference; sigmashift=8 is the doc default carried from the 5B. The refinermethod/refinercontrolpercentage
        # body keys are CONFIRMED in-repo (the image upscale path emits them); `refinermodel` is DERIVED via
        # SwarmUI's CleanTypeName from the source display name "Refiner Model" (lowercase letters only).
        #   ON-GPU / GATED (NOT verified at rest — no GPU in CI): (1) the `refinermodel` body key must be
        #   confirmed against a running SwarmUI's /API/ListT2IParameters; (2) steps + sampler/scheduler are NOT
        #   doc-sourced for the non-distilled 14B (SwarmUI's doc omits them) — uni_pc/simple + 20 steps carried
        #   from the 5B as the starting point, to be tuned live; (3) the 32GB fit of the dual ~9.65GB Q4_K_M
        #   experts in StepSwap + (4) the city96 ComfyUI-GGUF node install / GGUF arch auto-detect.
        'video' {
            if ($Fast)         { @{ model = 'ltxv-2b-0.9.8-distilled.safetensors'; textvideoframes = 97; steps = 8;  cfgscale = 1;   width = 768; height = 512; videofps = 24; videoformat = 'h264-mp4' } }
            # refinerupscale=1 (explicit, no resize): here the refiner group is reused for a noise-EXPERT
            # StepSwap (a denoising handoff from the high- to the low-noise expert), NOT the hi-res upscale the
            # image -Upscale/-Refine path uses it for — so it must NOT inherit that path's refinerupscale=2.
            # (Whether SwarmUI's StepSwap even reads refinerupscale is part of the on-GPU confirm.)
            elseif ($Quality)  { @{ model = 'Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf'; refinermethod = 'StepSwap'; refinermodel = 'Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf'; refinercontrolpercentage = 0.5; refinerupscale = 1; textvideoframes = 49; steps = 20; cfgscale = 5; width = 832; height = 480; videofps = 24; videoformat = 'h264-mp4'; sampler = 'uni_pc'; scheduler = 'simple'; sigmashift = 8 } }
            else               { @{ model = 'wan2.2_ti2v_5B_fp16.safetensors';     textvideoframes = 49; steps = 20; cfgscale = 3.5; width = 832; height = 480; videofps = 24; videoformat = 'h264-mp4'; sampler = 'uni_pc'; scheduler = 'simple'; sigmashift = 8 } }
        }
        # music DEFAULT = ACE-Step 1.5 turbo (fast: 10 steps / CFG 1). Unlike image/video, turbo is the music
        # default and hi-fi is OPT-IN -> -Quality (not -Fast) swaps to ACE-Step 1.5 XL base with the OFFICIAL
        # ComfyUI example-workflow params: steps 50 / cfg 6 / euler / simple (template KSampler node id 3
        # widgets_values=[50,6,"euler","simple"], ModelSamplingAuraFlow shift=3 left to SwarmUI's backend default).
        # NOTE cfg=6 is the official XL-*base* value (a community 7.3 exists ONLY for the XL *SFT* finetune) —
        # pending on-GPU confirmation. textaudiobpm/duration keep the DokiDex defaults (-Bpm/-Duration override).
        'music' {
            if ($Quality) { @{ model = 'acestep_v1.5_xl_base_bf16.safetensors'; textaudiobpm = 128; textaudioduration = 10; steps = 50; cfgscale = 6; sampler = 'euler'; scheduler = 'simple' } }
            else           { @{ model = 'acestep_v1.5_turbo.safetensors';          textaudiobpm = 128; textaudioduration = 10; steps = 10; cfgscale = 1 } }
        }
        'edit'  { @{ model = 'qwen_image_edit_2511_fp8mixed.safetensors'; steps = 20; cfgscale = 2.5 } }
        # image->video: generate a frame (Z-Image Turbo, fast seed) then animate it via the native videomodel
        # pipeline. The videosteps/videocfg/videoresolution trio is what makes the I2V step fire (per
        # docs/wiki/11-media-recipes.md); add -InitImage to animate an EXISTING still instead of a fresh frame.
        'i2v'   { @{ model = 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors'; steps = 8; cfgscale = 1; width = 832; height = 480; videomodel = 'wan2.2_ti2v_5B_fp16.safetensors'; videoframes = 49; videosteps = 20; videocfg = 3.5; videofps = 24; videoresolution = 'Image'; videoformat = 'h264-mp4' } }
        # video + synced SFX via the WanFoley custom ComfyUI workflow -> one muxed mp4 with 48 kHz audio.
        'foley' { @{ comfyuicustomworkflow = 'WanFoley'; seed = -1 } }
        # face-identity transfer via the InstantID custom ComfyUI workflow (SDXL): pass the reference face as the
        # init image (doki gen -FaceId -InitImage <face.png>). Ergonomic alias for -Workflow InstantID; the
        # workflow JSON wires the init image to InstantID's FaceAnalysis/LoadImage node. GATED: needs the -FaceId
        # install (node + weights) AND the on-GPU-authored CustomWorkflows\InstantID.json (see docs/decisions.md).
        'faceid' { @{ comfyuicustomworkflow = 'InstantID' } }
        # face-identity transfer via the PuLID-Flux custom ComfyUI workflow (FLUX.1-dev): pass the reference face
        # as the init image (doki gen -Pulid -InitImage <face.png>). Ergonomic alias for -Workflow PuLID; the
        # workflow JSON wires the init image to the PuLID-Flux apply node. GATED: needs the -Pulid install (balazik
        # node + the non-gated FLUX fp8 base + pulid_flux weights) AND the on-GPU-authored CustomWorkflows\PuLID.json
        # (which must FIRST verify the Alpha/stale node loads on the current ComfyUI) — see docs/decisions.md.
        'pulid' { @{ comfyuicustomworkflow = 'PuLID' } }
        # audio-driven talking-video via the InfiniteTalk custom ComfyUI workflow (MeiGen InfiniteTalk on the
        # Wan2.1-I2V-14B base, run through Kijai's WanVideoWrapper): the portrait rides the init-image channel
        # (-InitImage <portrait>) and the driving voice rides the -Audio channel (-Audio <wav/mp3>). Ergonomic
        # alias for -Workflow InfiniteTalk; the workflow JSON wires the portrait + audio to the wrapper's image/
        # audio-load nodes. GATED: needs the -InfiniteTalk install (node + ~82GB base + adapter + wav2vec2) AND
        # the on-GPU-authored CustomWorkflows\InfiniteTalk.json (the audio body-key is pinned there) — see docs/decisions.md.
        'infinitetalk' { @{ comfyuicustomworkflow = 'InfiniteTalk' } }
        # LIGHT lip-sync via the LatentSync custom ComfyUI workflow (ByteDance LatentSync 1.5, run through the
        # ShmuelRonen/ComfyUI-LatentSyncWrapper node) — the LIGHTER alternative to InfiniteTalk (~9.5GB on disk /
        # 8GB VRAM vs InfiniteTalk's ~82GB base). I/O DIVERGENCE vs InfiniteTalk: LatentSync RE-SYNCS an EXISTING
        # clip's mouth to new audio (VIDEO-in), it does NOT generate a talking video from a portrait. So it takes
        # the driving voice via -Audio <wav/mp3> ONLY; the source video rides the workflow's own video-input channel
        # (NO mandatory portrait -InitImage — that is the InfiniteTalk contract, not this one). Ergonomic alias for
        # -Workflow LatentSync; the audio rides the same provisional inputaudio body-key InfiniteTalk parks (pinned
        # on-GPU once the authored workflow names its audio-load node). GATED: needs the -LatentSync install (node +
        # the ByteDance/LatentSync-1.5 weights) AND the on-GPU-authored CustomWorkflows\LatentSync.json — see
        # docs/decisions.md.
        'latentsync' { @{ comfyuicustomworkflow = 'LatentSync' } }
        # GATED TTS-Audio-Suite alternative speech (15 engines + RVC) via a per-ENGINE ComfyUI custom workflow.
        # The chosen engine selects WHICH workflow JSON runs (one per engine, e.g. TtsSuite-IndexTTS2 /
        # TtsSuite-Higgs), so the recipe resolves -Engine -> comfyuicustomworkflow='TtsSuite-<engine>' exactly the
        # way 'foley'/'infinitetalk' resolve to a single workflow. The text rides the standard ${prompt} injection
        # point; an optional reference voice clip rides the audio body-key (-Audio, the same provisional inputaudio
        # key InfiniteTalk parks — pinned on-GPU once the authored workflow names its audio-load node). Default
        # engine = IndexTTS2 (duration/emotion control). Engine is normalized (strip non-alphanumerics, THEN
        # case-fold to a canonical token) so 'IndexTTS-2'/'IndexTTS2'/'indextts2'/'index tts 2' ALL resolve to the
        # SAME workflow name (TtsSuite-IndexTTS2) — a known-engine table maps the stripped-lowercase token to its
        # canonical casing; an unknown engine keeps its stripped token (the other ~13 engines still pass through).
        # GATED: needs the -TtsSuite install (node; weights auto-download on first use) AND the on-GPU-authored
        # CustomWorkflows\TtsSuite-<engine>.json (see docs/decisions.md). This NEVER touches the :8004 Chatterbox
        # default (chat readback / api-speak) — that's a different transport.
        'speech' {
            $eng = if ($Engine) { (Resolve-TtsEngine $Engine) } else { 'IndexTTS2' }
            @{ comfyuicustomworkflow = "TtsSuite-$eng"; seed = -1 }
        }
    }
    # -Upscale = pure 4x-UltraSharp post pass (control% 0 regenerates NO detail). -Refine = a real hi-res-fix:
    # same upscaler but control% 0.35 regenerates coherent detail, + tiling to cap VRAM on the DiT model.
    # Still images only; -Refine wins if both are passed.
    if (($Upscale -or $Refine) -and $Kind -in @('image', 'edit')) {
        $r.refinermethod = 'PostApply'
        $r.refinercontrolpercentage = $(if ($Refine) { 0.35 } else { 0 })
        $r.refinerupscale = 2
        # content-class engine selector: a named engine -> its upscaler model, OR a raw model filename verbatim
        # (lets any installed upscaler be picked). Default/balanced = the shipped 4x-UltraSharp (unchanged).
        $engines = @{ balanced = 'model-4x-UltraSharp.pth'; photo = 'model-4x-UltraSharp.pth'; anime = '4x-AnimeSharp.pth'; illustration = '4x-AnimeSharp.pth' }
        $r.refinerupscalemethod = $(if (-not $Upscaler) { 'model-4x-UltraSharp.pth' } elseif ($engines.ContainsKey($Upscaler.ToLower())) { $engines[$Upscaler.ToLower()] } else { $Upscaler })
        if ($Refine) { $r.refinerdotiling = $true }
    }
    return $r
}

# place the user's idea into the right field(s) for the kind: image/video wrap the lazy idea in
# <mpprompt:...> so the always-on :8013 rewriter expands it at generate time (unless -Raw); music maps the
# idea to the audio style with an [instrumental] prompt; edit uses the idea as a literal edit instruction.
# -Realism / -Face are opt-in SwarmUI prompt TAGS (processed by SwarmUI itself, independent of the rewriter):
# they're appended AFTER the <mpprompt:..> wrapper on the image-family kinds only (image / edit / i2v).
function Get-GenPromptFields {
    param(
        [Parameter(Mandatory)][string]$Kind, [Parameter(Mandatory)][string]$Idea,
        [switch]$Raw, [switch]$Face, [switch]$Realism, [string]$Lyrics, [string]$Lora, [string]$Segment
    )
    # base on-disk name (no extension) of the Z-Image realism LoRA in SwarmUI's Models\Lora (setup.ps1).
    $RealismLora = 'Z-Image-Realism'
    switch ($Kind) {
        # music: the idea is the STYLE/genre; the prompt field carries lyrics (ACE-Step sings them) or
        # [instrumental] for no vocals. -Lyrics turns the instrumental composer into a song composer.
        'music' { return @{ prompt = $(if ($Lyrics) { $Lyrics } else { '[instrumental]' }); textaudiostyle = $Idea } }
        'edit'  { $p = $Idea }
        # speech (TTS-Audio-Suite): the idea is the LITERAL text to speak — it must ride ${prompt} verbatim,
        # NOT through the :8013 cinematic <mpprompt:..> rewriter (that would rewrite the words being spoken).
        'speech' { return @{ prompt = $Idea } }
        default { $p = $(if ($Raw) { $Idea } else { "<mpprompt:$Idea>" }) }
    }
    # SwarmUI tags ride OUTSIDE the rewriter wrapper. -Realism applies a Z-Image realism LoRA; -Face runs the
    # CLIP-text Segment system as an inpaint face-refine (the ADetailer equivalent). image / edit / i2v only.
    if ($Kind -in @('image', 'edit', 'i2v')) {
        if ($Realism) { $p += " <lora:$($RealismLora):0.7>" }
        if ($Face)    { $p += ' <segment:face,0.4,0.5>' }
        # -Lora "name:0.8,other" -> <lora:name:0.8> tags (the LoRA mixer); bare name defaults to weight 1.
        if ($Lora) {
            foreach ($entry in ($Lora -split ',')) {
                $e = $entry.Trim(); if (-not $e) { continue }
                $name = $e; $wt = '1'
                $ci = $e.LastIndexOf(':')
                if ($ci -gt 0) {
                    $maybe = $e.Substring($ci + 1).Trim()
                    if ($maybe -match '^\d+(\.\d+)?$') { $name = $e.Substring(0, $ci).Trim(); $wt = $maybe }
                }
                if ($name) { $p += " <lora:$($name):$wt>" }
            }
        }
        # -Segment "hair, hands:0.6" -> <segment:hair,0.4,0.5> tags (promptable region refine; generalizes
        # -Face to any keyword). Each entry: keyword or keyword:creativity (default creativity 0.4, threshold 0.5).
        if ($Segment) {
            foreach ($entry in ($Segment -split ',')) {
                $e = $entry.Trim(); if (-not $e) { continue }
                $kw = $e; $cr = '0.4'
                $ci = $e.LastIndexOf(':')
                if ($ci -gt 0) {
                    $maybe = $e.Substring($ci + 1).Trim()
                    if ($maybe -match '^\d+(\.\d+)?$') { $kw = $e.Substring(0, $ci).Trim(); $cr = $maybe }
                }
                if ($kw) { $p += " <segment:$($kw),$cr,0.5>" }
            }
        }
    }
    return @{ prompt = $p }
}

# FILENAME-keyed model-family param override: when the selected checkpoint belongs to a family whose sampler
# knobs differ from the kind recipe, return the params to apply OVER the recipe (applied in Build-GenBody right
# after the -Model swap). PURE + total: empty/unknown model -> @{} so EVERY existing path stays byte-for-byte
# unchanged (only models matched below ever diverge). GPU-free; the on-disk filename is SwarmUI's model key.
#
# IMAGE-ONLY (defense-in-depth): -Kind gates the override to the image recipe — the values below are an
# IMAGE-recipe replacement, so a non-image kind (e.g. a hand-typed `doki gen -Edit -Model flux-2-klein-4b…`)
# returns @{} and keeps its own recipe's steps/cfg/sampler intact rather than being clobbered.
#
# FLUX.2 Klein — the FLUX.2 family needs sampler=euler + the new "Flux2" specialty scheduler (NOT Z-Image's
# dpmpp_2m/karras), so a Klein checkpoint selected via -Model must override the Z-Image image recipe. The
# scheduler+sampler are AUTHORITATIVE per SwarmUI's docs/Model Support.md, which states verbatim "Scheduler:
# Defaults to Flux2, a new specialty scheduler added for Flux.2" and "Sampler: Defaults to Euler". The step
# counts come from the official ComfyUI text-to-image template (image_flux2_klein_text_to_image.json):
# distilled = 4 steps / CFG 1, base = 20 steps / CFG 5.
#   ON-GPU NOTE: the 4-step distilled count diverges from SwarmUI's own prose (which cites ~8 steps for the
#   distilled tier); the step counts here follow the ComfyUI template and, like all of these defaults, are
#   install/render-unverified at rest (no GPU in CI). The scheduler/sampler VALUES are doc-confirmed above.
function Get-ModelFamilyOverride {
    param(
        [string]$Model,
        [ValidateSet('image', 'video', 'music', 'edit', 'i2v', 'foley', 'faceid', 'pulid', 'infinitetalk', 'latentsync', 'speech')][string]$Kind = 'image'
    )
    if (-not $Model -or $Kind -ne 'image') { return @{} }
    if ($Model -like 'flux-2-klein*') {
        # NVFP4 caveat: flux-2-klein-4b-nvfp4.safetensors (BFL's OWN native FP4 sibling) also matches this glob and,
        # lacking a '*base*' infix, falls to the distilled branch below. BFL's nvfp4 model card states NO inference
        # config and does NOT label the file distilled-vs-base (the '-base-' naming convention was the Comfy-Org
        # repackage's, not BFL's nvfp4 repo) — so the distilled classification of the nvfp4 file is the CONSERVATIVE
        # assumption and is ON-GPU-UNVERIFIED (a 4-step-vs-base-step A/B is the labeled confirm). Kept conservative.
        if ($Model -like '*base*') { return @{ steps = 20; cfgscale = 5; sampler = 'euler'; scheduler = 'Flux2' } }
        else                       { return @{ steps = 4;  cfgscale = 1; sampler = 'euler'; scheduler = 'Flux2' } }
    }
    # Qwen-Image BASE (in-image-text) GGUF — the NON-distilled t2i unet, so it needs a REAL CFG (cfg 1 only
    # suits the distilled/Lightning preset). steps=20/cfg=4 are the SwarmUI-doc-blessed quality/speed band
    # (Model Support.md: "CFG=4 ... at a performance cost", "normal ~20 works"); sampler=euler + scheduler=
    # simple are doc/template-confirmed (the official image_qwen_image.json KSampler). The match can't collide
    # with the Edit-2511 checkpoint (qwen_image_edit_2511_fp8mixed.safetensors, routed via -Edit): -like is
    # case-INSENSITIVE, so the real discriminators are the hyphen in 'Qwen_Image-*' (the Edit file has an
    # underscore after 'image', not a hyphen) + the '.gguf' extension (Edit is .safetensors) — the Edit name
    # fails BOTH. ON-GPU NOTE (render-unverified at rest — no GPU in CI): the GGUF arch auto-detect + the
    # one-time city96/ComfyUI-GGUF node install popup (headless-accepted via setup.ps1's InstallConfirmWS) +
    # the exact step/cfg within the doc-supported 20-50 / cfg 4 band + the live 32GB fit are the confirms; the
    # values below are additive (image-kind only) so every non-Qwen path stays byte-for-byte unchanged.
    if ($Model -like 'Qwen_Image-*.gguf') { return @{ steps = 20; cfgscale = 4; sampler = 'euler'; scheduler = 'simple' } }
    # Qwen-Image BASE as a Nunchaku NVFP4 single-file checkpoint (svdq-fp4_*-qwen-image.safetensors, ~3x faster
    # on Blackwell/RTX-50xx than the GGUF/bf16 path — see the gated setup.ps1 -Nunchaku block + docs/decisions.md).
    # The NVFP4 variant is the SAME non-distilled t2i unet as the GGUF above, so it SHARES the base's sampler band
    # (steps 20 / cfg 4 / euler / simple) — only the on-disk quant + the loader differ, not the recipe. The .gguf
    # match above is extension-locked and does NOT catch the .safetensors NVFP4 file, so this is purely additive.
    #   - Keyed on 'svdq-*qwen-image.safetensors' (the nunchaku-tech NVFP4 naming): svdq-fp4 = Blackwell NVFP4,
    #     svdq-int4 = the pre-Blackwell INT4 build — both match here and want the same base knobs.
    #   - EXCLUDES the 4-step/8-step Lightning fp4 files (*lightning*): those are low-step distills that want
    #     cfg=1 / steps 4-8 instead, so they must NOT inherit this base steps=20/cfg=4 band. The default install
    #     fetches only the non-Lightning base (svdq-fp4_r128-qwen-image.safetensors); add a Lightning branch here
    #     only if a Lightning fp4 file is fetched. The '*base*'/.gguf discriminators that protect the Qwen-Edit
    #     checkpoint still hold — svdq names contain neither, so no collision with the -Edit path.
    # ON-GPU NOTE (render-unverified at rest — no GPU in CI): whether SwarmUI exposes the nunchaku loader as a
    # plain -Model swap (this path) or needs a dedicated Nunchaku-loader custom workflow is the labeled on-GPU
    # confirm; if it needs a workflow, Qwen NVFP4 becomes a -Workflow hook instead. The values here are additive
    # (image-kind only) so every non-svdq path stays byte-for-byte unchanged.
    if ($Model -like 'svdq-*qwen-image.safetensors' -and $Model -notlike '*lightning*') { return @{ steps = 20; cfgscale = 4; sampler = 'euler'; scheduler = 'simple' } }
    # Z-Image-TURBO as a Nunchaku NVFP4 single-file checkpoint (svdq-*z-image-turbo.safetensors, nunchaku-ai/
    # nunchaku-z-image-turbo — Z-Image-Turbo 4-bit support landed in nunchaku v1.1.0, perf-boosted in v1.2.0).
    # This is the SAME architecture as DokiDex's #1 photoreal + real-time-canvas BASE (Z-Image-Turbo, the -Fast
    # image tier), so it MUST inherit the TURBO recipe, NOT the Z-Image BASE default. The -Fast image recipe above
    # uses steps 8 / cfg 1 / euler / simple (and carries NO curated negative — Turbo is a low-step distill), so the
    # svdq Turbo checkpoint reuses exactly those knobs; this override drops the BASE's 35/4.5/dpmpp_2m+karras+negative.
    #   - Keyed on 'svdq-*z-image-turbo.safetensors': matches the svdq-fp4 (Blackwell NVFP4) build the install
    #     fetches plus the int4 / r32 / r256 rank siblings in the same repo — all the same Turbo arch, same knobs.
    #   - The '-turbo' suffix is load-bearing: the plain Z-Image BASE default (z_image_bf16.safetensors) is NOT an
    #     svdq file and does NOT contain 'z-image-turbo', so it still returns @{} (its 35/4.5 path stays byte-for-byte).
    # ON-GPU NOTE (render-unverified at rest — no GPU in CI): like the Qwen svdq above, whether SwarmUI exposes the
    # nunchaku loader as a plain -Model swap (this path) or needs a dedicated Nunchaku-loader workflow is the labeled
    # on-GPU confirm. The values here are additive (image-kind only) so every non-svdq path stays byte-for-byte unchanged.
    if ($Model -like 'svdq-*z-image-turbo.safetensors') { return @{ steps = 8; cfgscale = 1; sampler = 'euler'; scheduler = 'simple' } }
    return @{}
}

# merge recipe + prompt fields + session (+ optional init image) into the final GenerateText2Image body.
function Build-GenBody {
    param(
        [Parameter(Mandatory)][hashtable]$Recipe,
        [Parameter(Mandatory)][hashtable]$PromptFields,
        [Parameter(Mandatory)][string]$SessionId,
        [string]$InitImageB64, [string]$MaskImageB64, [string]$AudioB64,
        [int]$Seed = -1, [int]$Count = 1, [double]$Strength = -1, [string]$Aspect,
        [int]$Duration = 0, [int]$Bpm = 0, [string]$Negative,
        [string]$ControlNets,
        [string]$EndImageB64, [bool]$Reference = $false, [double]$RefWeight = 0.6,
        [string]$Interpolate, [int]$InterpolateMult = 2, [string]$Workflow, [string]$Tile, [string]$Model,
        [ValidateSet('image', 'video', 'music', 'edit', 'i2v', 'foley', 'faceid', 'pulid', 'infinitetalk', 'latentsync', 'speech')][string]$Kind = 'image'
    )
    $body = @{ session_id = $SessionId; images = $(if ($Count -gt 1) { $Count } else { 1 }) }
    foreach ($kv in $Recipe.GetEnumerator())      { $body[$kv.Key] = $kv.Value }
    foreach ($kv in $PromptFields.GetEnumerator()) { $body[$kv.Key] = $kv.Value }
    # model override (manual picker / Auto router): replace the recipe's default checkpoint with a chosen one.
    # Then apply the checkpoint's FAMILY param override (e.g. FLUX.2 Klein -> euler/Flux2) OVER the kind recipe.
    # Empty map for every non-matched model, so Z-Image / Turbo / Chroma / anime / video / music / edit paths
    # are byte-for-byte unchanged; this fires ONLY when the chosen -Model belongs to an overriding family.
    if ($Model) {
        $body.model = $Model
        # FAMILY override is IMAGE-recipe-only (Get-ModelFamilyOverride returns @{} for non-image kinds), so a
        # Klein checkpoint hand-picked under -Edit/-Video keeps that kind's own recipe knobs untouched.
        $ov = Get-ModelFamilyOverride -Model $Model -Kind $Kind
        foreach ($kv in $ov.GetEnumerator()) { $body[$kv.Key] = $kv.Value }
        # Low-step distilled checkpoints carry no curated negative -> drop the Z-Image BASE negative so it isn't
        # silently carried onto them (a user -Negative still sets it below). Two families qualify: FLUX.2 Klein
        # (euler/Flux2, real CFG but ships no negative) and the Nunchaku Z-Image-TURBO NVFP4 (svdq-*z-image-turbo,
        # the cfg-1 Turbo distill — the -Fast Turbo recipe omits the negative, so its NVFP4 sibling must too).
        # Qwen svdq is deliberately NOT here: it runs at a real cfg 4 and keeps the curated negative (see its note).
        # Gated to the image kind to match the override above (non-image recipes keep their own negative handling).
        if ($Kind -eq 'image' -and ($Model -like 'flux-2-klein*' -or $Model -like 'svdq-*z-image-turbo.safetensors') -and $body.ContainsKey('negativeprompt')) { $body.Remove('negativeprompt') }
    }
    if ($Seed -ge 0) { $body.seed = $Seed }   # >=0 = reproducible; omit (-1) lets SwarmUI pick a random seed
    # an init image turns text2image into img2img / image-edit; creativity 0 = keep the input faithfully,
    # higher = more variation (the -Strength "vary" dial). Defaults to 0 when no strength is given.
    if ($InitImageB64) { $body.initimage = $InitImageB64; $body.initimagecreativity = $(if ($Strength -ge 0) { $Strength } else { 0 }) }
    if ($MaskImageB64) { $body.maskimage = $MaskImageB64 }   # white = the inpaint region (edit canvas)
    # InfiniteTalk driving audio (base64). The custom-workflow audio-input body key is the on-GPU authoring
    # confirm (it only binds once CustomWorkflows\InfiniteTalk.json names its audio-load node); `inputaudio` is
    # the provisional key, to be pinned against the authored workflow. Carried only when an audio clip is given.
    if ($AudioB64) { $body.inputaudio = $AudioB64 }
    if ($Aspect) {   # aspect-ratio preset -> width/height (caller passes it only for image/edit)
        switch ($Aspect) {
            '16:9' { $body.width = 1344; $body.height = 768 }
            '9:16' { $body.width = 768;  $body.height = 1344 }
            '4:3'  { $body.width = 1152; $body.height = 896 }
            '3:4'  { $body.width = 896;  $body.height = 1152 }
            default { $body.width = 1024; $body.height = 1024 }
        }
    }
    if ($Duration -gt 0) { $body.textaudioduration = $Duration }   # music: track length in seconds (recipe default 10)
    if ($Bpm -gt 0)      { $body.textaudiobpm = $Bpm }             # music: tempo override (recipe default 128)
    if ($Negative) {   # user negative prompt: append to the recipe's negative (image) or set it (else)
        $body.negativeprompt = $(if ($body.ContainsKey('negativeprompt') -and $body.negativeprompt) { "$($body.negativeprompt), $Negative" } else { $Negative })
    }
    # ControlNet stacking (up to 3 units; JSON array of {Model,Image(base64),Strength,Preprocessor}). Keys are
    # SwarmUI-source-confirmed via CleanTypeName (lowercase letters only of "ControlNet [Two|Three] Model /
    # Strength / Image Input / Preprocessor") -> controlnet / controlnettwo / controlnetthree prefixes.
    if ($ControlNets) {
        $prefixes = @('controlnet', 'controlnettwo', 'controlnetthree')
        $units = @($ControlNets | ConvertFrom-Json)
        for ($i = 0; $i -lt $units.Count -and $i -lt 3; $i++) {
            $u = $units[$i]; if (-not $u.Model) { continue }
            $px = $prefixes[$i]
            $body."${px}model" = $u.Model
            $body."${px}strength" = $(if ($null -ne $u.Strength) { $u.Strength } else { 1 })
            if ($u.Image) { $body."${px}imageinput" = $u.Image }
            if ($u.Preprocessor) { $body."${px}preprocessor" = $u.Preprocessor }
        }
    }
    # FLF2V end keyframe (video/i2v). Key "Video End Image" -> videoendimage (SwarmUI CleanTypeName); the model
    # must support end frames (Wan FLF2V / LTX-V) — ignored by models that don't. Init image = the start frame.
    if ($EndImageB64) { $body.videoendimage = $EndImageB64 }
    # Image reference via IP-Adapter (style/subject transfer): the init image becomes an IP-Adapter revision
    # input. Keys from SwarmUI source ("Use IP-Adapter" id UseIPAdapterForRevision, "IP-Adapter Weight" id
    # IPAdapterWeight) -> useipadapterforrevision / ipadapterweight. Opt-in + needs the init image; if no
    # IP-Adapter is available for the base, SwarmUI ignores it and it degrades to plain img2img (not broken).
    if ($Reference -and $InitImageB64) { $body.useipadapterforrevision = $true; $body.ipadapterweight = $RefWeight }
    # Frame interpolation (video post). Keys "Video Frame Interpolation Method/Multiplier" (SwarmUI source) ->
    # videoframeinterpolationmethod / videoframeinterpolationmultiplier. Method = RIFE/FILM/GIMM; gated on the
    # interpolation node being installed (an InstallableFeature) — ignored otherwise.
    if ($Interpolate) { $body.videoframeinterpolationmethod = $Interpolate; $body.videoframeinterpolationmultiplier = $InterpolateMult }
    # Custom ComfyUI workflow (the same comfyuicustomworkflow hook foley uses): run ANY installed SwarmUI custom
    # workflow by name (SUPIR upscale, InstantID face-ref, or a user's own) with the standard inputs (prompt /
    # init image) mapped to it. Gated on the workflow being installed in SwarmUI; unknown name -> SwarmUI errors.
    if ($Workflow) { $body.comfyuicustomworkflow = $Workflow }
    # Seamless-tileable output (SwarmUI "Seamless Tileable" -> seamlesstileable; enum true/X-Only/Y-Only).
    if ($Tile) {
        $body.seamlesstileable = $(switch ($Tile.ToLower()) { 'true' { 'true' } 'both' { 'true' } 'x' { 'X-Only' } 'y' { 'Y-Only' } default { '' } })
        if (-not $body.seamlesstileable) { $body.Remove('seamlesstileable') }
    }
    return $body
}

# --- live orchestration (needs the card: `doki up media`) -------------------------------------------------
# Probe SwarmUI, expand+build the request from the pure helpers above, POST GenerateText2Image, report the
# artifact and (best-effort) open it. Deliberately does NOT auto-switch the GPU to media — that would evict a
# running LLM mid-session; if SwarmUI is down it tells the user to `doki up media` and stops.
function Invoke-Gen {
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [ValidateSet('image', 'video', 'music', 'edit', 'i2v', 'foley', 'faceid', 'pulid', 'infinitetalk', 'latentsync', 'speech')][string]$Kind = 'image',
        [string]$Engine,   # -Speak engine selector (IndexTTS2 / Higgs / RVC / ...): picks the TtsSuite-<engine> workflow
        [switch]$Fast, [switch]$Upscale, [switch]$Refine, [switch]$Quality, [switch]$Raw, [switch]$NoOpen,
        [switch]$Face, [switch]$Realism, [switch]$BodyOnly, [string]$Upscaler,
        [int]$Seed = -1, [int]$Count = 1, [double]$Strength = -1, [string]$Aspect,
        [string]$Lyrics, [int]$Duration = 0, [int]$Bpm = 0, [string]$Lora, [string]$Negative, [string]$Segment,
        [string]$ControlNets,
        [string]$EndImage, [switch]$Reference, [double]$RefWeight = 0.6,
        [string]$Interpolate, [int]$InterpolateMult = 2, [string]$Workflow, [string]$Tile, [string]$Model,
        [string]$InitImage, [string]$MaskImage, [string]$Audio, [string]$Out,
        [string]$Base = 'http://127.0.0.1:7801'
    )
    if ($Kind -eq 'edit' -and -not $InitImage) { throw "-Edit needs -InitImage <path-to-image>" }
    # InstantID is meaningless without a reference face, which rides the init-image channel — fail loudly up
    # front (mirrors -Edit) rather than POSTing an InstantID workflow with no face for SwarmUI to choke on.
    if ($Kind -eq 'faceid' -and -not $InitImage) { throw "-FaceId requires -InitImage <reference face>" }
    # PuLID-Flux is likewise meaningless without a reference face, which rides the init-image channel — fail loudly
    # up front (mirrors -Edit/-FaceId) rather than POSTing a PuLID workflow with no face for SwarmUI to choke on.
    if ($Kind -eq 'pulid' -and -not $InitImage) { throw "-Pulid requires -InitImage <reference face>" }
    # InfiniteTalk needs BOTH a portrait (init-image channel) AND a driving audio clip — fail loudly up front on
    # either (mirrors -Edit/-FaceId) rather than POSTing a talking-video workflow with a missing input.
    if ($Kind -eq 'infinitetalk' -and -not $InitImage) { throw "-InfiniteTalk requires -InitImage <portrait>" }
    if ($Kind -eq 'infinitetalk' -and -not $Audio)     { throw "-InfiniteTalk requires -Audio <wav/mp3>" }
    # LatentSync (LIGHT lip-sync) re-syncs an EXISTING clip's mouth to new audio (video-in), so it needs the
    # driving voice — fail loudly up front (mirrors the InfiniteTalk -Audio guard). Unlike InfiniteTalk it does
    # NOT require a portrait -InitImage: the source video rides the workflow's own video-input channel, so -Audio
    # is the only mandatory input here (the I/O divergence flagged on the latentsync recipe arm).
    if ($Kind -eq 'latentsync' -and -not $Audio)       { throw "-LatentSync requires -Audio <wav/mp3>" }
    # LatentSync is audio-driven VIDEO re-sync (it edits an EXISTING clip's mouth to the audio), so the source
    # video is supplied via the on-GPU workflow's own video-input channel — NOT via -InitImage (an image). Reject
    # -InitImage loudly rather than silently base64'ing it onto the body and ignoring it, which would mislead a
    # user into thinking a portrait/still drove the gen (the I/O divergence vs InfiniteTalk, which DOES take a
    # portrait via -InitImage). Fire up front, before the init-image file-read below.
    if ($Kind -eq 'latentsync' -and $InitImage)        { throw "-LatentSync is audio-driven video re-sync; the source video is supplied via the on-GPU workflow, not -InitImage" }
    # -Speak (TTS-Audio-Suite) needs TEXT to synthesize — fail loudly up front (mirrors -Edit/-FaceId) rather
    # than POSTing a speech workflow with nothing to speak. The text is the gen idea ($Prompt); a reference voice
    # clip is OPTIONAL (rides -Audio, the zero-shot clone input). $Prompt is already Mandatory, but guard the
    # whitespace-only case so the error names -Speak (the dispatcher fails earlier on an empty $Arg too).
    if ($Kind -eq 'speech' -and [string]::IsNullOrWhiteSpace($Prompt)) { throw "-Speak requires text to synthesize (the gen idea is the spoken text)" }
    if ($Upscale -and $Kind -notin @('image', 'edit')) { throw "-Upscale only applies to image/edit gens (got $Kind)" }
    if ($Refine -and $Kind -notin @('image', 'edit')) { throw "-Refine only applies to image/edit gens (got $Kind)" }
    if ($Face -and $Kind -notin @('image', 'edit', 'i2v')) { throw "-Face only applies to image/edit/i2v gens (got $Kind)" }
    if ($Realism -and $Kind -notin @('image', 'edit', 'i2v')) { throw "-Realism only applies to image/edit/i2v gens (got $Kind)" }
    if ($MaskImage -and $Kind -ne 'edit') { throw "-MaskImage only applies to -Edit gens (got $Kind)" }
    # -Audio rides the infinitetalk + latentsync + speech kinds (their driving / reference voice). Without this
    # guard, Build-GenBody injects the provisional `inputaudio` key whenever -Audio is set, so `doki gen -Video
    # -Audio x.wav` would silently smuggle a stray audio key into a Wan video body — symmetric with the -MaskImage
    # guard above. Fire BEFORE the audio file-read so a real clip on the wrong kind is rejected too (not waved
    # through by the existence check).
    # -Audio rides infinitetalk (driving voice) OR latentsync (the LIGHT lip-sync's driving voice) OR speech (the
    # optional zero-shot reference voice clip for the TTS-Audio-Suite engines). Any other kind would silently
    # smuggle the provisional `inputaudio` key into a non-audio body, so reject it loudly (symmetric with
    # -MaskImage). All three audio-consuming kinds are permitted.
    if ($Audio -and $Kind -notin @('infinitetalk', 'latentsync', 'speech')) { throw "-Audio only applies to -InfiniteTalk / -LatentSync / -Speak gens (got $Kind)" }
    $initB64 = $null
    if ($InitImage) {
        if (-not (Test-Path -LiteralPath $InitImage)) { throw "init image not found: $InitImage" }
        $initB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $InitImage).Path))
    }
    $maskB64 = $null
    if ($MaskImage) {
        if (-not (Test-Path -LiteralPath $MaskImage)) { throw "mask image not found: $MaskImage" }
        $maskB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $MaskImage).Path))
    }
    # InfiniteTalk driving audio: base64 the wav/mp3 here (Build-GenBody stays pure). The exact SwarmUI body key
    # for a custom-workflow audio input is the on-GPU authoring confirm (it only matters once InfiniteTalk.json
    # exists + names its audio-load node), so Build-GenBody parks it under a provisional `inputaudio` key.
    $audioB64 = $null
    if ($Audio) {
        if (-not (Test-Path -LiteralPath $Audio)) { throw "audio clip not found: $Audio" }
        $audioB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Audio).Path))
    }
    # ControlNet units arrive as a JSON array with image PATHS; read each to base64 here (Build-GenBody is pure).
    $controlNetsB64 = ''
    if ($ControlNets -and $Kind -in @('image', 'edit')) {
        $units = @($ControlNets | ConvertFrom-Json)
        foreach ($u in $units) {
            if ($u.Image -and (Test-Path -LiteralPath $u.Image)) {
                $u.Image = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $u.Image).Path))
            }
        }
        $controlNetsB64 = ($units | ConvertTo-Json -Depth 5 -Compress -AsArray)
    }
    $Prompt = Expand-References -Text $Prompt               # @name -> a saved references/<name>.txt snippet (before wildcards, so a ref can contain __wildcards__)
    $Prompt = Expand-Wildcards -Text $Prompt -Seed $Seed   # __name__ -> a media-assets/wildcards line; resolved prompt is what generates + records
    $aspectArg = $(if ($Kind -in @('image', 'edit')) { $Aspect } else { '' })   # aspect reshapes image/edit only; video dims are model-fixed
    $referenceArg = ($Reference.IsPresent -and $Kind -in @('image', 'edit'))   # IP-Adapter image reference: image/edit only
    $interpolateArg = $(if ($Kind -in @('video', 'i2v')) { $Interpolate } else { '' })   # frame interpolation: video/i2v only
    $tileArg = $(if ($Kind -in @('image', 'edit')) { $Tile } else { '' })   # seamless tiling: still-image only
    $endB64 = $null
    if ($EndImage -and $Kind -in @('video', 'i2v')) {   # FLF2V end keyframe: video/i2v only
        if (-not (Test-Path -LiteralPath $EndImage)) { throw "end image not found: $EndImage" }
        $endB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $EndImage).Path))
    }
    # music-only knobs (ACE-Step): lyrics replace [instrumental]; duration/bpm reshape the track. Ignored elsewhere.
    $lyricsArg   = $(if ($Kind -eq 'music') { $Lyrics } else { '' })
    $durationArg = $(if ($Kind -eq 'music') { $Duration } else { 0 })
    $bpmArg      = $(if ($Kind -eq 'music') { $Bpm } else { 0 })
    $loraArg     = $(if ($Kind -in @('image', 'edit', 'i2v')) { $Lora } else { '' })   # LoRA mixer: image-family only
    $segmentArg  = $(if ($Kind -in @('image', 'edit', 'i2v')) { $Segment } else { '' })   # promptable segment: image-family only
    # -BodyOnly: print the exact GenerateText2Image body (recipe + prompt fields + optional init image) and
    # stop — no session, no SwarmUI call. The web host injects session_id after GetNewSession and drives
    # GenerateText2ImageWS itself for live progress, so the recipe stays single-sourced here.
    if ($BodyOnly) {
        $recipe = Get-GenRecipe -Kind $Kind -Fast:$Fast -Upscale:$Upscale -Refine:$Refine -Quality:$Quality -Upscaler $Upscaler -Engine $Engine
        $fields = Get-GenPromptFields -Kind $Kind -Idea $Prompt -Raw:$Raw -Face:$Face -Realism:$Realism -Lyrics $lyricsArg -Lora $loraArg -Segment $segmentArg
        $b = Build-GenBody -Recipe $recipe -PromptFields $fields -SessionId 'pending' -InitImageB64 $initB64 -MaskImageB64 $maskB64 -AudioB64 $audioB64 -Seed $Seed -Count $Count -Strength $Strength -Aspect $aspectArg -Duration $durationArg -Bpm $bpmArg -Negative $Negative -ControlNets $controlNetsB64 -EndImageB64 $endB64 -Reference $referenceArg -RefWeight $RefWeight -Interpolate $interpolateArg -InterpolateMult $InterpolateMult -Workflow $Workflow -Tile $tileArg -Model $Model -Kind $Kind
        $b.Remove('session_id')   # placeholder only; the web host injects the real session_id after GetNewSession
        return ($b | ConvertTo-Json -Depth 6 -Compress)
    }

    # SwarmUI must already be in media mode — don't contend for the GPU / evict the LLM behind the user's back.
    try { Invoke-WebRequest "$Base/" -TimeoutSec 4 -UseBasicParsing | Out-Null }
    catch { throw "SwarmUI not reachable at $Base — start media mode first:  .\doki.ps1 up media" }

    $tag = "$Kind$(if ($Fast) { ' (fast)' })$(if ($Quality) { ' (hi-fi)' })$(if ($Upscale) { ' +4x' })$(if ($Refine) { ' +refine' })$(if ($Realism) { ' +realism' })$(if ($Face) { ' +face' })"
    Write-Host "[gen] $tag  <-  ""$Prompt""" -ForegroundColor Cyan

    $recipe = Get-GenRecipe -Kind $Kind -Fast:$Fast -Upscale:$Upscale -Refine:$Refine -Quality:$Quality -Upscaler $Upscaler
    $fields = Get-GenPromptFields -Kind $Kind -Idea $Prompt -Raw:$Raw -Face:$Face -Realism:$Realism -Lyrics $lyricsArg -Lora $loraArg -Segment $segmentArg
    $sid = (Invoke-RestMethod "$Base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
    $body = (Build-GenBody -Recipe $recipe -PromptFields $fields -SessionId $sid -InitImageB64 $initB64 -MaskImageB64 $maskB64 -AudioB64 $audioB64 -Seed $Seed -Count $Count -Strength $Strength -Aspect $aspectArg -Duration $durationArg -Bpm $bpmArg -Negative $Negative -ControlNets $controlNetsB64 -EndImageB64 $endB64 -Reference $referenceArg -RefWeight $RefWeight -Interpolate $interpolateArg -InterpolateMult $InterpolateMult -Workflow $Workflow -Tile $tileArg -Model $Model -Kind $Kind) | ConvertTo-Json -Depth 6
    $resp = Invoke-RestMethod "$Base/API/GenerateText2Image" -Method Post -ContentType 'application/json' -TimeoutSec 600 -Body $body
    $artifacts = @($resp.images)
    if (-not $artifacts) { throw "SwarmUI returned no artifact ($($resp | ConvertTo-Json -Depth 4 -Compress))" }

    # video/music gens also return a preview still first — prefer the real media file when present.
    $primary = @($artifacts | Where-Object { $_ -match '\.(mp4|webm|gif|mp3|wav|flac)$' })[0]
    if (-not $primary) { $primary = $artifacts[0] }
    Write-Host "[gen] -> $primary" -ForegroundColor Green

    $url = if ($primary -match '^https?://') { $primary } elseif ($primary -match '^View/') { "$Base/$primary" } else { "$Base/View/$primary" }
    if ($Out) { try { Invoke-WebRequest $url -OutFile $Out -UseBasicParsing; Write-Host "[gen] saved -> $Out" } catch { Write-Warning "save failed: $($_.Exception.Message)" } }
    if (-not $NoOpen) { try { Start-Process $url } catch { } }
    return $primary
}
