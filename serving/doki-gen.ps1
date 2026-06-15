# serving/doki-gen.ps1 — `doki gen "<idea>"` text->media.
#
# Split into PURE helpers (Resolve-GenKind / Get-GenRecipe / Get-GenPromptFields / Build-GenBody) that carry
# the docs/media-recipes.md table 1:1 and are exercised with NO GPU by tests/doki-gen.test.ps1, plus the
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

# kind (+ -Fast / -Upscale modifiers) -> the SwarmUI body fields for that recipe (model + sampler knobs),
# verbatim from docs/media-recipes.md. No prompt, no session — Build-GenBody merges those in later.
function Get-GenRecipe {
    param(
        [ValidateSet('image', 'video', 'music', 'edit', 'i2v', 'foley')][string]$Kind = 'image',
        [switch]$Fast, [switch]$Upscale
    )
    $r = switch ($Kind) {
        'image' { @{ model = 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors'; steps = 8; cfgscale = 1; width = 1024; height = 1024 } }
        'video' {
            if ($Fast) { @{ model = 'ltxv-2b-0.9.8-distilled.safetensors'; textvideoframes = 97; steps = 8;  cfgscale = 1;   width = 768; height = 512; videofps = 24; videoformat = 'h264-mp4' } }
            else       { @{ model = 'wan2.2_ti2v_5B_fp16.safetensors';     textvideoframes = 49; steps = 20; cfgscale = 3.5; width = 832; height = 480; videofps = 24; videoformat = 'h264-mp4' } }
        }
        'music' { @{ model = 'acestep_v1.5_turbo.safetensors'; textaudiobpm = 128; textaudioduration = 10; steps = 10; cfgscale = 1 } }
        'edit'  { @{ model = 'qwen_image_edit_2511_fp8mixed.safetensors'; steps = 20; cfgscale = 2.5 } }
        # image->video: generate a frame (Z-Image) then animate it via the native videomodel pipeline. The
        # videosteps/videocfg/videoresolution trio is what makes the I2V step fire (per media-recipes.md);
        # add -InitImage to animate an EXISTING still instead of a fresh frame.
        'i2v'   { @{ model = 'SwarmUI_Z-Image-Turbo-FP8Mix.safetensors'; steps = 8; cfgscale = 1; width = 832; height = 480; videomodel = 'wan2.2_ti2v_5B_fp16.safetensors'; videoframes = 25; videosteps = 20; videocfg = 3.5; videofps = 24; videoresolution = 'Image'; videoformat = 'h264-mp4' } }
        # video + synced SFX via the WanFoley custom ComfyUI workflow -> one muxed mp4 with 48 kHz audio.
        'foley' { @{ comfyuicustomworkflow = 'WanFoley'; seed = -1 } }
    }
    # -Fast on a still image just trims steps (Z-Image Turbo is already low; floor at 6). Video -Fast picked
    # the distilled LTXV model above instead. Music/edit ignore -Fast.
    if ($Fast -and $Kind -eq 'image') { $r.steps = 6 }
    # -Upscale = the 4x-UltraSharp post pass (still images only). control% 0 = pure upscale, no refine pass;
    # it only fires when refinermethod + refinercontrolpercentage are BOTH set.
    if ($Upscale -and $Kind -in @('image', 'edit')) {
        $r.refinermethod = 'PostApply'; $r.refinercontrolpercentage = 0
        $r.refinerupscale = 2; $r.refinerupscalemethod = 'model-4x-UltraSharp.pth'
    }
    return $r
}

# place the user's idea into the right field(s) for the kind: image/video wrap the lazy idea in
# <mpprompt:...> so the always-on :8013 rewriter expands it at generate time (unless -Raw); music maps the
# idea to the audio style with an [instrumental] prompt; edit uses the idea as a literal edit instruction.
function Get-GenPromptFields {
    param([Parameter(Mandatory)][string]$Kind, [Parameter(Mandatory)][string]$Idea, [switch]$Raw)
    switch ($Kind) {
        'music' { return @{ prompt = '[instrumental]'; textaudiostyle = $Idea } }
        'edit'  { return @{ prompt = $Idea } }
        default { return @{ prompt = $(if ($Raw) { $Idea } else { "<mpprompt:$Idea>" }) } }
    }
}

# merge recipe + prompt fields + session (+ optional init image) into the final GenerateText2Image body.
function Build-GenBody {
    param(
        [Parameter(Mandatory)][hashtable]$Recipe,
        [Parameter(Mandatory)][hashtable]$PromptFields,
        [Parameter(Mandatory)][string]$SessionId,
        [string]$InitImageB64
    )
    $body = @{ session_id = $SessionId; images = 1 }
    foreach ($kv in $Recipe.GetEnumerator())      { $body[$kv.Key] = $kv.Value }
    foreach ($kv in $PromptFields.GetEnumerator()) { $body[$kv.Key] = $kv.Value }
    # an init image turns text2image into img2img / image-edit; creativity 0 = keep the input faithfully.
    if ($InitImageB64) { $body.initimage = $InitImageB64; $body.initimagecreativity = 0 }
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
        [switch]$Fast, [switch]$Upscale, [switch]$Raw, [switch]$NoOpen,
        [string]$InitImage, [string]$Out,
        [string]$Base = 'http://127.0.0.1:7801'
    )
    if ($Kind -eq 'edit' -and -not $InitImage) { throw "-Edit needs -InitImage <path-to-image>" }
    if ($Upscale -and $Kind -notin @('image', 'edit')) { throw "-Upscale only applies to image/edit gens (got $Kind)" }
    $initB64 = $null
    if ($InitImage) {
        if (-not (Test-Path -LiteralPath $InitImage)) { throw "init image not found: $InitImage" }
        $initB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $InitImage).Path))
    }
    # SwarmUI must already be in media mode — don't contend for the GPU / evict the LLM behind the user's back.
    try { Invoke-WebRequest "$Base/" -TimeoutSec 4 -UseBasicParsing | Out-Null }
    catch { throw "SwarmUI not reachable at $Base — start media mode first:  .\doki.ps1 up media" }

    $tag = "$Kind$(if ($Fast) { ' (fast)' })$(if ($Upscale) { ' +4x' })"
    Write-Host "[gen] $tag  <-  ""$Prompt""" -ForegroundColor Cyan

    $recipe = Get-GenRecipe -Kind $Kind -Fast:$Fast -Upscale:$Upscale
    $fields = Get-GenPromptFields -Kind $Kind -Idea $Prompt -Raw:$Raw
    $sid = (Invoke-RestMethod "$Base/API/GetNewSession" -Method Post -Body '{}' -ContentType 'application/json').session_id
    $body = (Build-GenBody -Recipe $recipe -PromptFields $fields -SessionId $sid -InitImageB64 $initB64) | ConvertTo-Json -Depth 6
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
