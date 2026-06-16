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
    param([switch]$Video, [switch]$Music, [switch]$Edit, [switch]$I2v, [switch]$Foley)
    $picked = @()
    if ($Video) { $picked += 'video' }
    if ($Music) { $picked += 'music' }
    if ($Edit)  { $picked += 'edit'  }
    if ($I2v)   { $picked += 'i2v'   }
    if ($Foley) { $picked += 'foley' }
    if ($picked.Count -gt 1) { throw "pick ONE of -Video / -Music / -Edit / -I2v / -Foley (got: $($picked -join ', '))" }
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

# kind (+ -Fast / -Upscale modifiers) -> the SwarmUI body fields for that recipe (model + sampler knobs),
# verbatim from docs/wiki/11-media-recipes.md. No prompt, no session — Build-GenBody merges those in later.
function Get-GenRecipe {
    param(
        [ValidateSet('image', 'video', 'music', 'edit', 'i2v', 'foley')][string]$Kind = 'image',
        [switch]$Fast, [switch]$Upscale, [switch]$Refine, [string]$Upscaler
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
        'video' {
            if ($Fast) { @{ model = 'ltxv-2b-0.9.8-distilled.safetensors'; textvideoframes = 97; steps = 8;  cfgscale = 1;   width = 768; height = 512; videofps = 24; videoformat = 'h264-mp4' } }
            else       { @{ model = 'wan2.2_ti2v_5B_fp16.safetensors';     textvideoframes = 49; steps = 20; cfgscale = 3.5; width = 832; height = 480; videofps = 24; videoformat = 'h264-mp4'; sampler = 'uni_pc'; scheduler = 'simple'; sigmashift = 8 } }
        }
        'music' { @{ model = 'acestep_v1.5_turbo.safetensors'; textaudiobpm = 128; textaudioduration = 10; steps = 10; cfgscale = 1 } }
        'edit'  { @{ model = 'qwen_image_edit_2511_fp8mixed.safetensors'; steps = 20; cfgscale = 2.5 } }
        # image->video: generate a frame (Z-Image Turbo, fast seed) then animate it via the native videomodel
        # pipeline. The videosteps/videocfg/videoresolution trio is what makes the I2V step fire (per
        # docs/wiki/11-media-recipes.md); add -InitImage to animate an EXISTING still instead of a fresh frame.
        'i2v'   { @{ model = 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors'; steps = 8; cfgscale = 1; width = 832; height = 480; videomodel = 'wan2.2_ti2v_5B_fp16.safetensors'; videoframes = 49; videosteps = 20; videocfg = 3.5; videofps = 24; videoresolution = 'Image'; videoformat = 'h264-mp4' } }
        # video + synced SFX via the WanFoley custom ComfyUI workflow -> one muxed mp4 with 48 kHz audio.
        'foley' { @{ comfyuicustomworkflow = 'WanFoley'; seed = -1 } }
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

# merge recipe + prompt fields + session (+ optional init image) into the final GenerateText2Image body.
function Build-GenBody {
    param(
        [Parameter(Mandatory)][hashtable]$Recipe,
        [Parameter(Mandatory)][hashtable]$PromptFields,
        [Parameter(Mandatory)][string]$SessionId,
        [string]$InitImageB64, [string]$MaskImageB64,
        [int]$Seed = -1, [int]$Count = 1, [double]$Strength = -1, [string]$Aspect,
        [int]$Duration = 0, [int]$Bpm = 0, [string]$Negative,
        [string]$ControlImageB64, [string]$ControlModel, [double]$ControlStrength = 1, [string]$ControlPreprocessor,
        [string]$EndImageB64, [bool]$Reference = $false, [double]$RefWeight = 0.6,
        [string]$Interpolate, [int]$InterpolateMult = 2
    )
    $body = @{ session_id = $SessionId; images = $(if ($Count -gt 1) { $Count } else { 1 }) }
    foreach ($kv in $Recipe.GetEnumerator())      { $body[$kv.Key] = $kv.Value }
    foreach ($kv in $PromptFields.GetEnumerator()) { $body[$kv.Key] = $kv.Value }
    if ($Seed -ge 0) { $body.seed = $Seed }   # >=0 = reproducible; omit (-1) lets SwarmUI pick a random seed
    # an init image turns text2image into img2img / image-edit; creativity 0 = keep the input faithfully,
    # higher = more variation (the -Strength "vary" dial). Defaults to 0 when no strength is given.
    if ($InitImageB64) { $body.initimage = $InitImageB64; $body.initimagecreativity = $(if ($Strength -ge 0) { $Strength } else { 0 }) }
    if ($MaskImageB64) { $body.maskimage = $MaskImageB64 }   # white = the inpaint region (edit canvas)
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
    # ControlNet (structure conditioning). Keys are SwarmUI-source-confirmed via CleanTypeName (lowercase
    # letters only of the display name): "ControlNet Model/Strength/Image Input/Preprocessor". A model selection
    # activates it; the control image is a SEPARATE input (not the init image), so structure guides a fresh gen.
    if ($ControlModel) {
        $body.controlnetmodel = $ControlModel
        $body.controlnetstrength = $ControlStrength
        if ($ControlImageB64) { $body.controlnetimageinput = $ControlImageB64 }
        if ($ControlPreprocessor) { $body.controlnetpreprocessor = $ControlPreprocessor }
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
    return $body
}

# --- live orchestration (needs the card: `doki up media`) -------------------------------------------------
# Probe SwarmUI, expand+build the request from the pure helpers above, POST GenerateText2Image, report the
# artifact and (best-effort) open it. Deliberately does NOT auto-switch the GPU to media — that would evict a
# running LLM mid-session; if SwarmUI is down it tells the user to `doki up media` and stops.
function Invoke-Gen {
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [ValidateSet('image', 'video', 'music', 'edit', 'i2v', 'foley')][string]$Kind = 'image',
        [switch]$Fast, [switch]$Upscale, [switch]$Refine, [switch]$Raw, [switch]$NoOpen,
        [switch]$Face, [switch]$Realism, [switch]$BodyOnly, [string]$Upscaler,
        [int]$Seed = -1, [int]$Count = 1, [double]$Strength = -1, [string]$Aspect,
        [string]$Lyrics, [int]$Duration = 0, [int]$Bpm = 0, [string]$Lora, [string]$Negative, [string]$Segment,
        [string]$ControlImage, [string]$ControlModel, [double]$ControlStrength = 1, [string]$ControlPreprocessor,
        [string]$EndImage, [switch]$Reference, [double]$RefWeight = 0.6,
        [string]$Interpolate, [int]$InterpolateMult = 2,
        [string]$InitImage, [string]$MaskImage, [string]$Out,
        [string]$Base = 'http://127.0.0.1:7801'
    )
    if ($Kind -eq 'edit' -and -not $InitImage) { throw "-Edit needs -InitImage <path-to-image>" }
    if ($Upscale -and $Kind -notin @('image', 'edit')) { throw "-Upscale only applies to image/edit gens (got $Kind)" }
    if ($Refine -and $Kind -notin @('image', 'edit')) { throw "-Refine only applies to image/edit gens (got $Kind)" }
    if ($Face -and $Kind -notin @('image', 'edit', 'i2v')) { throw "-Face only applies to image/edit/i2v gens (got $Kind)" }
    if ($Realism -and $Kind -notin @('image', 'edit', 'i2v')) { throw "-Realism only applies to image/edit/i2v gens (got $Kind)" }
    if ($MaskImage -and $Kind -ne 'edit') { throw "-MaskImage only applies to -Edit gens (got $Kind)" }
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
    $controlB64 = $null
    if ($ControlImage) {
        if (-not (Test-Path -LiteralPath $ControlImage)) { throw "control image not found: $ControlImage" }
        $controlB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ControlImage).Path))
    }
    $Prompt = Expand-Wildcards -Text $Prompt -Seed $Seed   # __name__ -> a media-assets/wildcards line; resolved prompt is what generates + records
    $aspectArg = $(if ($Kind -in @('image', 'edit')) { $Aspect } else { '' })   # aspect reshapes image/edit only; video dims are model-fixed
    $controlModelArg = $(if ($Kind -in @('image', 'edit')) { $ControlModel } else { '' })   # ControlNet: image/edit only
    $referenceArg = ($Reference.IsPresent -and $Kind -in @('image', 'edit'))   # IP-Adapter image reference: image/edit only
    $interpolateArg = $(if ($Kind -in @('video', 'i2v')) { $Interpolate } else { '' })   # frame interpolation: video/i2v only
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
        $recipe = Get-GenRecipe -Kind $Kind -Fast:$Fast -Upscale:$Upscale -Refine:$Refine -Upscaler $Upscaler
        $fields = Get-GenPromptFields -Kind $Kind -Idea $Prompt -Raw:$Raw -Face:$Face -Realism:$Realism -Lyrics $lyricsArg -Lora $loraArg -Segment $segmentArg
        $b = Build-GenBody -Recipe $recipe -PromptFields $fields -SessionId 'pending' -InitImageB64 $initB64 -MaskImageB64 $maskB64 -Seed $Seed -Count $Count -Strength $Strength -Aspect $aspectArg -Duration $durationArg -Bpm $bpmArg -Negative $Negative -ControlImageB64 $controlB64 -ControlModel $controlModelArg -ControlStrength $ControlStrength -ControlPreprocessor $ControlPreprocessor -EndImageB64 $endB64 -Reference $referenceArg -RefWeight $RefWeight -Interpolate $interpolateArg -InterpolateMult $InterpolateMult
        $b.Remove('session_id')   # placeholder only; the web host injects the real session_id after GetNewSession
        return ($b | ConvertTo-Json -Depth 6 -Compress)
    }

    # SwarmUI must already be in media mode — don't contend for the GPU / evict the LLM behind the user's back.
    try { Invoke-WebRequest "$Base/" -TimeoutSec 4 -UseBasicParsing | Out-Null }
    catch { throw "SwarmUI not reachable at $Base — start media mode first:  .\doki.ps1 up media" }

    $tag = "$Kind$(if ($Fast) { ' (fast)' })$(if ($Upscale) { ' +4x' })$(if ($Refine) { ' +refine' })$(if ($Realism) { ' +realism' })$(if ($Face) { ' +face' })"
    Write-Host "[gen] $tag  <-  ""$Prompt""" -ForegroundColor Cyan

    $recipe = Get-GenRecipe -Kind $Kind -Fast:$Fast -Upscale:$Upscale -Refine:$Refine -Upscaler $Upscaler
    $fields = Get-GenPromptFields -Kind $Kind -Idea $Prompt -Raw:$Raw -Face:$Face -Realism:$Realism -Lyrics $lyricsArg -Lora $loraArg -Segment $segmentArg
    $sid = (Invoke-RestMethod "$Base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
    $body = (Build-GenBody -Recipe $recipe -PromptFields $fields -SessionId $sid -InitImageB64 $initB64 -MaskImageB64 $maskB64 -Seed $Seed -Count $Count -Strength $Strength -Aspect $aspectArg -Duration $durationArg -Bpm $bpmArg -Negative $Negative -ControlImageB64 $controlB64 -ControlModel $controlModelArg -ControlStrength $ControlStrength -ControlPreprocessor $ControlPreprocessor -EndImageB64 $endB64 -Reference $referenceArg -RefWeight $RefWeight -Interpolate $interpolateArg -InterpolateMult $InterpolateMult) | ConvertTo-Json -Depth 6
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
