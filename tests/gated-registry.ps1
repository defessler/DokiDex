# tests/gated-registry.ps1 — the single source of truth for DokiDex's GATED integrations.
#
# Dot-sourced by BOTH `doki verify -Gated` (verify.ps1, with the real Test-Path) AND tests/verify-gated.test.ps1
# (with a stubbed path-exists predicate, so the coverage + status logic runs GPU-less with every multi-GB weight
# declared absent). It is PARSE-ONLY — it defines data + one pure function and runs no install/render side effects.
#
# Each entry describes ONE gated integration the session shipped (a setup.ps1 -Flag sidecar):
#   Flag     - the setup.ps1 / doki.ps1 -<Flag> switch that installs it
#   NodeDir  - the ComfyUI custom_nodes clone dir, RELATIVE to $swarm (media\SwarmUI). $null for venv sidecars.
#   VenvRoot - for the standalone venv sidecars (-Sam/-Demucs/-Train), the dir RELATIVE to $swarm's PARENT ($root).
#   Files    - the expected weight files (paths RELATIVE to $swarm) — the on-disk artifacts -Gated Test-Paths.
#              Empty for pip-only sidecars whose engines auto-download (-Demucs/-Train/-TtsSuite).
#   Workflow - the runnable SwarmUI API-prompt JSON (RELATIVE to $root) that the live render needs; $null when the
#              integration ships no JSON (the on-GPU authoring step). Get-GatedStatus reports its absence distinctly.
#   Models   - 'full' marks entries whose weights only land under setup.ps1 -Models full (Nunchaku svdq).
#   OnGpu    - the labeled on-GPU TODO (author workflow / confirm fit / test render) decisions.md records, printed
#              verbatim by -Gated so the runbook surfaces what still needs a GPU pass.
#
# Node paths mirror setup.ps1: $nodes = $swarm\dlbackend\comfy\ComfyUI\custom_nodes ; weights for the ComfyUI
# integrations live under $cmodels = $swarm\dlbackend\comfy\ComfyUI\models\... ; LatentSync's land under the node's
# own checkpoints\ ; the venv sidecars live under $root\audio-tools\... . All are on-disk artifacts to Test-Path.

$nodes   = 'dlbackend\comfy\ComfyUI\custom_nodes'
$cmodels = 'dlbackend\comfy\ComfyUI\models'
$smodels = 'Models'   # SwarmUI's OWN Models\ tree (where the main -Media checkpoints AND the Nunchaku svdq weights land, via setup.ps1 $diff)

$GatedRegistry = [ordered]@{

    'InstantID (-FaceId)' = @{
        Flag    = 'FaceId'
        NodeDir = "$nodes\ComfyUI_InstantID"
        Files   = @(
            "$cmodels\instantid\ip-adapter.bin"
            "$cmodels\controlnet\instantid_controlnet.safetensors"
            "$cmodels\insightface\models\antelopev2\glintr100.onnx"   # antelopev2 sentinel (1 of 5 .onnx)
        )
        Workflow = 'media-assets\InstantID.json'
        OnGpu    = 'Author InstantID.json (cubiq UI-graph -> SwarmUI API-prompt); confirm node load + a face-ID render + the antelopev2 5-onnx contents.'
    }

    'InfiniteTalk (-InfiniteTalk)' = @{
        Flag    = 'InfiniteTalk'
        NodeDir = "$nodes\ComfyUI-WanVideoWrapper"
        Files   = @(
            "$cmodels\diffusion_models\Wan2_1-InfiniTetalk-Single_fp16.safetensors"   # adapter (upstream typo intentional)
            "$cmodels\wav2vec2\chinese-wav2vec2-base\pytorch_model.bin"
            "$cmodels\wav2vec2\chinese-wav2vec2-base\preprocessor_config.json"
            "$cmodels\diffusion_models\Wan2_1-I2V-14B-480P_diffusion_pytorch_model-00001-of-00007.safetensors"   # 1st of 7 shards of the ~82GB base
            "$cmodels\vae\Wan2_1_VAE.pth"
        )
        Workflow = 'media-assets\InfiniteTalk.json'
        OnGpu    = 'Heaviest TODO: author InfiniteTalk.json (Kijai UI-graph -> API-prompt, wire image+audio); confirm the 32GB fit (fp16 14B OOMs -> fp8 + block-swap + 81f/25-overlap chunking, UNCONFIRMED); source an fp8 Wan2.1-I2V-14B repack; pin the audio body-key; confirm base TE/clip/VAE path-routing under the raw ComfyUI tree.'
    }

    'LatentSync (-LatentSync)' = @{
        Flag    = 'LatentSync'
        NodeDir = "$nodes\ComfyUI-LatentSyncWrapper"
        Files   = @(   # LatentSync weights live under the NODE's own checkpoints\ tree
            "$nodes\ComfyUI-LatentSyncWrapper\checkpoints\latentsync_unet.pt"
            "$nodes\ComfyUI-LatentSyncWrapper\checkpoints\stable_syncnet.pt"
            "$nodes\ComfyUI-LatentSyncWrapper\checkpoints\whisper\tiny.pt"
            "$nodes\ComfyUI-LatentSyncWrapper\checkpoints\vae\diffusion_pytorch_model.safetensors"   # REQUIRED sd-vae-ft-mse
        )
        Workflow = 'media-assets\LatentSync.json'
        OnGpu    = 'Author LatentSync.json; confirm node-load; wire the VIDEO-IN channel (video-in re-sync, audio-only, no portrait); pin the audio body-key; confirm the auxiliary\ filenames vs the node first-run; test render (8GB/1.5 fits 32GB).'
    }

    'PuLID-Flux (-Pulid)' = @{
        Flag    = 'Pulid'
        NodeDir = "$nodes\ComfyUI-PuLID-Flux"
        Files   = @(
            "$cmodels\diffusion_models\flux1-dev-fp8.safetensors"
            "$cmodels\clip\t5xxl_fp8_e4m3fn.safetensors"
            "$cmodels\clip\clip_l.safetensors"
            "$cmodels\vae\flux-ae.safetensors"
            "$cmodels\pulid\pulid_flux_v0.9.1.safetensors"
            "$cmodels\insightface\models\antelopev2\glintr100.onnx"   # SHARED with -FaceId (same sentinel)
        )
        Workflow = 'media-assets\PuLID.json'
        OnGpu    = 'FIRST confirm the Alpha/stale balazik node (last commit 2024-10-03) even LOADS on current ComfyUI (gates everything); THEN author PuLID.json (repoint to fp8 base + separate t5xxl/clip_l/ae, inject ${prompt} + reference-face init-image); confirm render quality (EVA02-CLIP auto-downloads first run).'
    }

    'Nunchaku NVFP4 (-Nunchaku)' = @{
        Flag    = 'Nunchaku'
        NodeDir = "$nodes\ComfyUI-nunchaku"
        Models  = 'full'
        Files   = @(   # NVFP4 svdq weights only land under -Models full; the runtime is a pip wheel (no on-disk weight).
                       # UNLIKE the FaceId/InfiniteTalk/PuLID ComfyUI weights ($cmodels, the raw tree), setup.ps1 writes
                       # these into SwarmUI's OWN Models\diffusion_models ($diff, line 443/1103-1104) — same dir as the
                       # z_image/klein checkpoints — so they root at $smodels, NOT $cmodels.
            "$smodels\diffusion_models\svdq-fp4_r128-z-image-turbo.safetensors"
            "$smodels\diffusion_models\svdq-fp4_r128-qwen-image.safetensors"
        )
        Workflow = $null   # no JSON copy — the svdq load-path is the on-GPU unknown
        OnGpu    = 'Confirm which torch+CUDA the live comfy env has -> which wheel (probed); THE SVDQ LOAD-PATH — does SwarmUI load the single-file svdq via its -Model picker OR the node loader / a custom workflow (if a workflow, the NVFP4 weights become a -Workflow hook); the NVFP4 step/cfg band + distilled-vs-base Klein call; the real Blackwell speedup on 32GB.'
    }

    'TTS-Audio-Suite (-TtsSuite)' = @{
        Flag     = 'TtsSuite'
        NodeDir  = "$nodes\TTS-Audio-Suite"
        Files    = @()   # all 15 engines auto-download on first use — nothing pre-fetched
        Workflow = $null # per-engine media-assets\TtsSuite-*.json is the on-GPU authoring step
        OnGpu    = 'Author per-engine TtsSuite-<engine>.json (e.g. IndexTTS2/Higgs) from the UI-graphs; CONFIRM SwarmUI''s image/video-centric CustomWorkflow runner can host a pure-TTS node returning a WAV at all (may need an audio-output mapping that is not OOTB); pin the auto-download paths + per-engine text/voice injection node names.'
    }

    'Kokoro fast/light TTS (-Kokoro)' = @{
        Flag     = 'Kokoro'
        VenvRoot = 'kokoro\Kokoro-FastAPI\.venv'   # standalone OpenAI-compatible server (remsky/Kokoro-FastAPI)
        Files    = @()   # the Kokoro-82M weights auto-download on first `.\doki.ps1 up` (nothing pre-fetched, like -TtsSuite)
        Workflow = $null # no SwarmUI JSON — Kokoro is a standalone :8006 server, not a ComfyUI node
        OnGpu    = 'Confirm the remsky/Kokoro-FastAPI server boots on :8006 (uvicorn api.src.main:app) and a /v1/audio/speech synth returns a WAV; the Kokoro-82M weights auto-download on first .\doki.ps1 up.'
    }

    'Scanned-PDF OCR (-Ocr)' = @{
        Flag       = 'Ocr'
        # A winget SYSTEM binary (UB-Mannheim Tesseract), not a ComfyUI node (NodeDir) nor a venv sidecar (VenvRoot):
        # the install is an absolute Program Files path, not under $swarm/$root. Get-GatedStatus has a SystemFile
        # branch: Ready when the binary exists, else NotInstalled. The pip parsers (pymupdf/pytesseract/Pillow) ride
        # the uv doc_ingest_bin overlay — no pre-fetched on-disk weight — so Files is empty; there is no SwarmUI JSON.
        SystemFile = 'C:\Program Files\Tesseract-OCR\tesseract.exe'
        Files      = @()
        Workflow   = $null
        OnGpu      = 'Installed, no render gate (CPU-only OCR; no labeled on-GPU step). Confirm: a scanned PDF ingests >0 chunks end-to-end via doc_ingest_bin; pytesseract resolves the binary at the Program Files path on a fresh box.'
    }

    'Demucs (-Demucs)' = @{
        Flag     = 'Demucs'
        VenvRoot = 'audio-tools\demucs\.venv'
        Files    = @()   # pip demucs only — no pre-fetched weights
        Workflow = $null
        OnGpu    = 'Installed, no render gate (CPU/standalone stem separation; no labeled on-GPU step).'
    }

    'SAM (-Sam)' = @{
        Flag     = 'Sam'
        VenvRoot = 'audio-tools\sam\.venv'
        Files    = @('audio-tools\sam\sam_vit_b.pth')   # ~375MB checkpoint
        Workflow = $null
        OnGpu    = 'Installed, no render gate (semantic click->mask; CPU/standalone, no labeled on-GPU step).'
    }

    'LoRA training (-Train)' = @{
        Flag     = 'Train'
        VenvRoot = 'audio-tools\sd-scripts\.venv'
        Files    = @()   # kohya sd-scripts clone — no pre-fetched weights
        Workflow = $null
        OnGpu    = 'Installed, no render gate (kohya sd-scripts; standalone training, no labeled on-GPU step).'
    }
}

# The status ENUM — the single source of truth for the four classifier outcomes. Get-GatedStatus returns one of
# these values AND verify.ps1's status->PASS/SKIP switch matches against these SAME names, so a wording change can
# never silently fall through to a mislabel: the producer and the consumer share one token table. (The string
# VALUES are the human-readable status; tests assert them, the -Gated grid prints them.)
$GatedStatus = [ordered]@{
    NotInstalled = 'not installed'
    Partial      = 'partial'
    WorkflowTodo = 'installed; workflow is on-GPU TODO'
    Ready        = 'ready'
}

# Get-GatedStatus — the PURE classifier. Given a registry entry + an injected path-exists predicate, return a
# status token (one of $GatedStatus.*) with NO I/O of its own (the caller supplies Test-Path or a stub), so it is
# unit-testable GPU-less and degrades every absent multi-GB weight to $GatedStatus.NotInstalled rather than throwing.
#   $Exists : { param($path) -> [bool] }
#   returns : $GatedStatus.NotInstalled | .Partial | .WorkflowTodo | .Ready
function Get-GatedStatus {
    param(
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)] [scriptblock] $Exists
    )
    # A winget SYSTEM-binary integration (-Ocr: UB-Mannheim Tesseract) installs to an ABSOLUTE Program Files path,
    # not under $swarm/$root and with no NodeDir/VenvRoot/weights/workflow — so it is the simplest shape: Ready when
    # the binary exists on disk, else NotInstalled. Handle it first so the $swarm/$root rooting below never applies.
    if ($Entry.SystemFile) {
        return $(if (& $Exists $Entry.SystemFile) { $GatedStatus.Ready } else { $GatedStatus.NotInstalled })
    }

    # locate the install on disk: ComfyUI node dir (under $swarm) OR a standalone venv (under $root). Normalize
    # to full paths (collapse ..\) so the string the predicate sees is stable regardless of how the caller
    # rooted us — the live Test-Path is path-form agnostic, but a stubbed string-equality predicate is not.
    $root  = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $swarm = [System.IO.Path]::GetFullPath((Join-Path $root "media\SwarmUI"))
    $loc   = if ($Entry.NodeDir) { Join-Path $swarm $Entry.NodeDir }
             elseif ($Entry.VenvRoot) { Join-Path $root $Entry.VenvRoot }
             else { $null }

    $nodeOk = $loc -and (& $Exists $loc)
    if (-not $nodeOk) { return $GatedStatus.NotInstalled }   # degrade, never throw

    # weights resolve against the SAME base that locates the entry on disk: a NodeDir (ComfyUI) entry's Files are
    # RELATIVE to $swarm (the raw ComfyUI tree, the node's own checkpoints\, OR SwarmUI's own Models\); a VenvRoot
    # sidecar's Files are RELATIVE to $root (e.g. SAM's audio-tools\sam\sam_vit_b.pth lives next to its .venv under
    # $root, NOT under media\SwarmUI). Rooting every Files entry at $swarm — as a prior cut did — left the one
    # VenvRoot entry that ALSO ships a weight (SAM) reporting 'partial' forever even when installed.
    $fileBase    = if ($Entry.VenvRoot) { $root } else { $swarm }
    $weightFlags = @($Entry.Files | ForEach-Object { [bool](& $Exists (Join-Path $fileBase $_)) })
    if ($weightFlags -contains $false) { return $GatedStatus.Partial }

    # node + all weights present. If the integration needs a workflow JSON that isn't committed, that is the
    # on-GPU authoring step; otherwise it is as ready as a GPU-less check can certify.
    if ($Entry.Workflow -and -not (& $Exists (Join-Path $root $Entry.Workflow))) {
        return $GatedStatus.WorkflowTodo
    }
    return $GatedStatus.Ready
}
