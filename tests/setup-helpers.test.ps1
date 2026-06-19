# tests/setup-helpers.test.ps1 — regression tests for setup.ps1's failure-recovery helpers.
#
# These pin the behaviours the ultracode audit hardened (atomic model download, fail-loud pip,
# in-session PATH refresh). The helpers are pulled out of setup.ps1 BY AST, by name — the test
# exercises the real committed source with zero duplication, so it can't silently drift, and we
# never run setup's install body (no side effects). Framework-free: exit 0 = all pass, 1 = fail.
#
# This file exists because the first draft of the Get-Model fix shipped `-C - --remove-on-error`
# together — curl rejects that combo (exit 2), which would have failed EVERY download. A test
# would have caught it; now one does. Run via `doki test`.

$ErrorActionPreference = "Stop"
$setup = Join-Path $PSScriptRoot "..\setup.ps1"
if (-not (Test-Path $setup)) { Write-Error "setup.ps1 not found at $setup"; exit 2 }

# --- import the real helpers by name (parse only; setup's body never executes) ---
# Extract each function's source text by AST, then dot-source the lot once AT SCRIPT SCOPE so
# the definitions persist for the test body (dot-sourcing inside a helper would scope them away).
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $setup).Path, [ref]$null, [ref]$null)
$fnText = @('Sync-Path', 'Pip', 'Get-Model') | ForEach-Object {
    $name = $_
    $fn = $ast.FindAll({ param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $x.Name -eq $name }, $true) | Select-Object -First 1
    if (-not $fn) { throw "function '$name' not found in setup.ps1 (renamed? the test must track it)" }
    $fn.Extent.Text
}
. ([scriptblock]::Create($fnText -join "`n`n"))

# Get-Model logs via Info/Ok/Warn — capture them (clean output + lets us assert the skip path).
$script:msgs = [System.Collections.Generic.List[string]]::new()
function Info($m) { $script:msgs.Add("INFO $m") }
function Ok($m)   { $script:msgs.Add("OK $m") }
function Warn($m) { $script:msgs.Add("WARN $m") }

# --- assertions ---
$script:pass = 0; $script:fail = 0
function Assert($cond, $msg) {
    if ($cond) { $script:pass++; Write-Host "  [PASS] $msg" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
}
function Assert-Throws($block, $msg) {
    $threw = $false; try { & $block } catch { $threw = $true }
    Assert $threw $msg
}

# --- scratch payload + a local file:// "server" (deterministic, offline) ---
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("doki-setup-tests-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
$src = Join-Path $work "source-model.bin"
[System.IO.File]::WriteAllText($src, ("MODELDATA" * 2048))   # ~18 KB, deterministic
$srcUrl  = ([Uri]$src).AbsoluteUri
$srcHash = (Get-FileHash $src).Hash
$srcLen  = (Get-Item $src).Length

try {
    Write-Host "`nSync-Path"
    $orig = $env:Path
    $env:Path = ""
    Sync-Path
    Assert ($env:Path.Length -gt 0) "refreshes an emptied PATH from the registry"
    Assert ($env:Path -match ';')   "joins machine + user PATH entries"
    $env:Path = $orig

    Write-Host "`nPip (fail-loud contract — line 20 mutes native errors, so it must check itself)"
    $pyOk  = Join-Path $work "py-ok.cmd";   Set-Content $pyOk  "@exit /b 0" -Encoding ascii
    $pyBad = Join-Path $work "py-fail.cmd"; Set-Content $pyBad "@exit /b 7" -Encoding ascii
    # Pip runs `& $py -m pip @args`; these .cmd stand-ins ignore the args and pick the exit code.
    Assert-Throws { Pip $pyBad install something } "throws when pip exits non-zero"
    $quiet = $true; try { Pip $pyOk install something } catch { $quiet = $false }
    Assert $quiet "returns quietly when pip exits zero"

    Write-Host "`nGet-Model (atomic .part -> Move-Item)"
    # success
    $d1 = Join-Path $work "got1.bin"
    Get-Model $srcUrl $d1
    Assert (Test-Path $d1)                       "success: promotes the download to dest"
    Assert (-not (Test-Path "$d1.part"))         "success: leaves no .part behind"
    Assert ((Get-FileHash $d1).Hash -eq $srcHash) "success: dest is byte-identical to source"

    # already-have (existence gate -> skip, no clobber)
    $script:msgs.Clear()
    Get-Model $srcUrl $d1
    Assert (($script:msgs | Where-Object { $_ -like 'OK have *' }).Count -ge 1) "skip: existing dest reported 'have', not re-fetched"
    Assert (-not (Test-Path "$d1.part"))         "skip: no .part created on the skip path"

    # failure (unreachable url) -> no dest, no orphan .part
    # ROBUSTNESS: a missing file:// URL makes curl exit 37, and on PowerShell 7.4/7.5 — where
    # $PSNativeCommandUseErrorActionPreference defaults to $true — a native non-zero exit under this file's
    # $ErrorActionPreference='Stop' throws a NativeCommandExitException INSIDE Get-Model (at the curl line, before
    # its own $LASTEXITCODE guard can Warn+return), which would abort the whole try{} and skip EVERY AST block
    # below (InstantID/Wan/FLUX2/Qwen/InfiniteTalk model-add asserts). setup.ps1 itself is immune because it sets
    # $PSNativeCommandUseErrorActionPreference=$false globally (line ~29); the test harness never did. Mirror that
    # native-error posture for JUST this deliberate-failure call (restoring it after) AND catch defensively, so the
    # curl/native-exit quirk records its intended outcome and the suite CONTINUES. Get-Model itself is untouched.
    $d2 = Join-Path $work "got2.bin"
    $badUrl = ([Uri](Join-Path $work "does-not-exist.bin")).AbsoluteUri
    $prevNativeEAP = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    try { Get-Model $badUrl $d2 } catch { $script:msgs.Add("WARN deliberate-failure Get-Model threw: $($_.Exception.Message)") }
    finally { $PSNativeCommandUseErrorActionPreference = $prevNativeEAP }
    Assert (-not (Test-Path $d2))                "failure: leaves no (truncated) dest file"
    Assert (-not (Test-Path "$d2.part"))         "failure: cleans up its own .part"

    # null url -> warn, create nothing
    $d3 = Join-Path $work "got3.bin"
    Get-Model $null $d3
    Assert (-not (Test-Path $d3))                "no-url: creates nothing"

    # resume: a leftover .part (hard-kill case) is continued, not restarted
    $d4 = Join-Path $work "got4.bin"
    $bytes = [System.IO.File]::ReadAllBytes($src)
    [System.IO.File]::WriteAllBytes("$d4.part", [byte[]]($bytes[0..2047]))   # first 2 KB already on disk
    Get-Model $srcUrl $d4
    Assert (Test-Path $d4)                        "resume: completes from a pre-existing .part"
    Assert ((Get-Item $d4).Length -eq $srcLen)    "resume: final file is the full length"
    Assert ((Get-FileHash $d4).Hash -eq $srcHash) "resume: resumed file is byte-identical to source"
    Assert (-not (Test-Path "$d4.part"))          "resume: .part promoted away"

    Write-Host "`nAnime SDXL pair (-Models full): well-formed HF resolve URLs + non-colliding local names"
    # Mine the REAL setup.ps1 for every `Get-Model <url> (Join-Path <dir> <file>)` call by AST, so this
    # tracks the committed source (no string-search drift, no running setup's body). Each call's first
    # positional arg is the URL; the local filename is the StringConstant arg of the Join-Path sub-expr.
    $calls = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Get-Model' }, $true)
    $entries = foreach ($c in $calls) {
        # CommandElements[0] is the bareword command name 'Get-Model' (also a StringConstant) — skip it; the
        # URL is the FIRST string arg after it (a plain literal, or an expandable "$base/..." string).
        $argEls = $c.CommandElements | Select-Object -Skip 1
        $urlEl  = $argEls | Where-Object { $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] -or $_ -is [System.Management.Automation.Language.ExpandableStringExpressionAst] } | Select-Object -First 1
        $url    = if ($urlEl -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $urlEl.Value } else { $urlEl.Extent.Text }
        # the dest is a (Join-Path $dir "<file>") paren-expression; grab its last string constant = the filename,
        # and the full paren extent text = the unique dir+file destination (so same-filename-different-subdir, e.g.
        # checkpoints/config.json vs checkpoints/vae/config.json, is NOT a false collision in the dupe-check below).
        $paren = $c.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.ParenExpressionAst] } | Select-Object -First 1
        $file  = $null
        $dest  = $null
        if ($paren) {
            $file = ($paren.FindAll({ param($y) $y -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | Select-Object -Last 1).Value
            $dest = ($paren.Extent.Text -replace '\s+', ' ')
        }
        [pscustomobject]@{ Url = $url; File = $file; Dest = $dest }
    }
    Assert ($entries.Count -ge 10) "setup.ps1 exposes the Get-Model download set ($($entries.Count) entries found)"

    # A canonical HF full-checkpoint resolve URL: https://huggingface.co/<owner>/<repo>/resolve/main/<name>.safetensors
    $hfResolve = '^https://huggingface\.co/[^/]+/[^/]+/resolve/main/.+\.safetensors$'
    foreach ($spec in @(
        @{ Url = 'https://huggingface.co/OnomaAIResearch/Illustrious-XL-v1.0/resolve/main/Illustrious-XL-v1.0.safetensors'; File = 'Illustrious-XL-v1.0.safetensors' },
        @{ Url = 'https://huggingface.co/cagliostrolab/animagine-xl-4.0/resolve/main/animagine-xl-4.0.safetensors';         File = 'Animagine-XL-4.0.safetensors' }
    )) {
        $e = $entries | Where-Object { $_.Url -eq $spec.Url } | Select-Object -First 1
        Assert ($null -ne $e)                       "anime SDXL: $($spec.File) is wired as a Get-Model entry"
        Assert ($spec.Url -match $hfResolve)        "anime SDXL: $($spec.File) URL is a well-formed HF resolve/main/*.safetensors URL"
        Assert ($e -and $e.File -eq $spec.File)     "anime SDXL: $($spec.File) saves under the expected stable local filename"
        # not a do_not_use / refiner / inpaint / component file — the canonical full checkpoint
        Assert ($spec.Url -notmatch '(?i)do_not_use|refiner|inpaint|/unet/|/vae/|-opt\.safetensors$') "anime SDXL: $($spec.File) targets the canonical full checkpoint (not a component/opt/do_not_use)"
    }

    Write-Host "`nFLUX.2 Klein (-Models full): NON-GATED Comfy-Org repackaged checkpoints, well-formed HF resolve URLs"
    # FLUX.2 Klein must be sourced from the NON-GATED Comfy-Org/flux2-klein-4B repo (the BFL repo returns 401);
    # the resolve/ split_files paths are HF-tree-verified. Pin both the distilled (default) and base (quality)
    # checkpoints by local filename, that they ride a Comfy-Org $flux2-rooted URL (NOT the gated BFL repo), and
    # that the $flux2 base resolves to a well-formed HF resolve/main/ URL. The URL in setup.ps1 is an
    # expandable "$flux2/diffusion_models/<file>" string, so match on its AST Extent.Text suffix (like Wan/ACE).
    $flux2Base = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$flux2' }, $true) | Select-Object -First 1)
    Assert ($null -ne $flux2Base) "FLUX.2 Klein: setup.ps1 defines a `$flux2 base URL var"
    $flux2Url = if ($flux2Base) { $flux2Base.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($flux2Url -match '^https://huggingface\.co/Comfy-Org/flux2-klein-4B/resolve/main/split_files$') "FLUX.2 Klein: `$flux2 = NON-GATED Comfy-Org resolve/main URL"
    foreach ($spec in @(
        @{ File = 'flux-2-klein-4b.safetensors' },
        @{ File = 'flux-2-klein-base-4b.safetensors' }
    )) {
        $e = $entries | Where-Object { $_.File -eq $spec.File } | Select-Object -First 1
        Assert ($null -ne $e)                       "FLUX.2 Klein: $($spec.File) is wired as a Get-Model entry"
        Assert ($e -and $e.Url -match '(?i)\$flux2/diffusion_models/') "FLUX.2 Klein: $($spec.File) URL hangs off the `$flux2 Comfy-Org base"
        # GATING GUARD: must never reference the 401-gated black-forest-labs repo
        Assert ($e -and $e.Url -notmatch '(?i)black-forest-labs') "FLUX.2 Klein: $($spec.File) does NOT point at the gated black-forest-labs repo"
        # resolved full URL (base + suffix) is a well-formed HF resolve/main/*.safetensors URL
        $full = ($e.Url -replace '(?i)\$flux2', $flux2Url).Trim('"')
        Assert ($full -match $hfResolve) "FLUX.2 Klein: $($spec.File) resolves to a well-formed HF resolve/main/*.safetensors URL"
    }
    Write-Host "`nWan 2.2 A14B GGUF (-Models full): QuantStack T2V high/low-noise Q4_K_M experts (the gated quality-video tier)"
    # The GATED quality-video tier swaps the 5B default -> the Wan 2.2 T2V A14B GGUF dual-expert pair (Q4_K_M,
    # ~9.65GB each) wired in SwarmUI's StepSwap. Pin BOTH experts by local filename, that they ride a $t2vgguf
    # QuantStack base (NOT a flat repo — the resolve URL includes the HighNoise/ or LowNoise/ subfolder), and
    # that $t2vgguf is a well-formed HF resolve/main URL. The GGUF filenames are HF-tree-verified (2026-06-19).
    $t2vgBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$t2vgguf' }, $true) | Select-Object -First 1)
    Assert ($null -ne $t2vgBase) "Wan A14B GGUF: setup.ps1 defines a `$t2vgguf base URL var"
    $t2vgUrl = if ($t2vgBase) { $t2vgBase.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($t2vgUrl -match '^https://huggingface\.co/QuantStack/Wan2\.2-T2V-A14B-GGUF/resolve/main$') "Wan A14B GGUF: `$t2vgguf = QuantStack T2V-A14B-GGUF resolve/main URL"
    foreach ($spec in @(
        @{ File = 'Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf'; Sub = 'HighNoise' },
        @{ File = 'Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf';  Sub = 'LowNoise' }
    )) {
        $e = $entries | Where-Object { $_.File -eq $spec.File } | Select-Object -First 1
        Assert ($null -ne $e)                       "Wan A14B GGUF: $($spec.File) is wired as a Get-Model entry"
        Assert ($e -and $e.Url -match '(?i)\$t2vgguf/') "Wan A14B GGUF: $($spec.File) URL hangs off the `$t2vgguf QuantStack base"
        # the resolve URL must include the expert's subfolder (the QuantStack tree is NOT flat)
        Assert ($e -and $e.Url -match "(?i)\$t2vgguf/$($spec.Sub)/") "Wan A14B GGUF: $($spec.File) URL includes its $($spec.Sub)/ subfolder (non-flat tree)"
        # resolved full URL is a well-formed HF resolve/main/*.gguf URL
        $full = ($e.Url -replace '(?i)\$t2vgguf', $t2vgUrl).Trim('"')
        Assert ($full -match '^https://huggingface\.co/[^/]+/[^/]+/resolve/main/.+\.gguf$') "Wan A14B GGUF: $($spec.File) resolves to a well-formed HF resolve/main/*.gguf URL"
    }

    Write-Host "`nQwen-Image base GGUF (-Models full): QuantStack Q4_K_M t2i unet (the in-image-text image tier)"
    # The Qwen-Image BASE (strong in-image TEXT) t2i unet, a QuantStack Q4_K_M GGUF (~13.1GB), HF-tree-verified.
    # It REUSES the Qwen2.5-VL TE + Qwen-Image VAE already pulled by the Edit-2511 block ($te/$vae targets), so
    # ONLY the unet is new. Pin it by local filename, that it rides a $qimggguf QuantStack base, and that the
    # base is a well-formed HF resolve/main URL. (Resolve 302->Xet CDN, content-disposition filename verified.)
    $qimggBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$qimggguf' }, $true) | Select-Object -First 1)
    Assert ($null -ne $qimggBase) "Qwen-Image GGUF: setup.ps1 defines a `$qimggguf base URL var"
    $qimggUrl = if ($qimggBase) { $qimggBase.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($qimggUrl -match '^https://huggingface\.co/QuantStack/Qwen-Image-GGUF/resolve/main$') "Qwen-Image GGUF: `$qimggguf = QuantStack Qwen-Image-GGUF resolve/main URL"
    $eQ = $entries | Where-Object { $_.File -eq 'Qwen_Image-Q4_K_M.gguf' } | Select-Object -First 1
    Assert ($null -ne $eQ)                       "Qwen-Image GGUF: Qwen_Image-Q4_K_M.gguf is wired as a Get-Model entry"
    Assert ($eQ -and $eQ.Url -match '(?i)\$qimggguf/') "Qwen-Image GGUF: URL hangs off the `$qimggguf QuantStack base"
    $qfull = ($eQ.Url -replace '(?i)\$qimggguf', $qimggUrl).Trim('"')
    Assert ($qfull -match '^https://huggingface\.co/[^/]+/[^/]+/resolve/main/.+\.gguf$') "Qwen-Image GGUF: resolves to a well-formed HF resolve/main/*.gguf URL"
    # the Qwen-Image BASE t2i unet must land in diffusion_models/ (NOT re-download a TE/VAE — those ride the
    # Edit-2511 block's $te/$vae targets; a second VAE under a different name would be a redundant duplicate).
    Assert ($eQ -and $eQ.File -eq 'Qwen_Image-Q4_K_M.gguf') "Qwen-Image GGUF: saves under the canonical QuantStack local filename"

    Write-Host "`nInstantID face-identity (-FaceId): cubiq node + InstantX weights wired off a `$ix base, into the ComfyUI model dirs"
    # The GATED face-ID sidecar (-FaceId) installs cubiq/ComfyUI_InstantID + ~4.55GB of InstantX weights and
    # REUSES the anime SDXL base already on disk (no FLUX). It is SDXL — PuLID-Flux (FLUX.1-dev ~22GB) was the
    # rejected alternative. The 3 weights hang off a $ix InstantX resolve/main base. Pin: (1) the node is cloned
    # from the cubiq repo, (2) $ix is the InstantX HF resolve/main base, (3) the IP-Adapter .bin + the ControlNet
    # safetensors + its config.json are wired Get-Model entries riding $ix, with non-colliding local filenames.
    # NOTE: this block does NOT register a workflow JSON — the runnable SwarmUI InstantID.json is the on-GPU
    # authoring step (the upstream example is UI-graph, not API-prompt), so there is intentionally no committed
    # media-assets\InstantID.json to assert here (see docs/decisions.md). Sizes/URLs HF-tree-verified this session.
    $idClone = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)cubiq/ComfyUI_InstantID' }, $true)
    Assert ($idClone.Count -ge 1) "InstantID: setup.ps1 clones the cubiq/ComfyUI_InstantID node (maintenance-mode/stable SDXL face-ID node)"

    $ixBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$ix' }, $true) | Select-Object -First 1)
    Assert ($null -ne $ixBase) "InstantID: setup.ps1 defines a `$ix InstantX base URL var"
    $ixUrl = if ($ixBase) { $ixBase.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($ixUrl -match '^https://huggingface\.co/InstantX/InstantID/resolve/main$') "InstantID: `$ix = InstantX/InstantID resolve/main URL"
    foreach ($spec in @(
        @{ File = 'ip-adapter.bin';                    Suffix = '/ip-adapter.bin' },
        @{ File = 'instantid_controlnet.safetensors';  Suffix = '/ControlNetModel/diffusion_pytorch_model.safetensors' },
        @{ File = 'instantid_controlnet_config.json';  Suffix = '/ControlNetModel/config.json' }
    )) {
        $e = $entries | Where-Object { $_.File -eq $spec.File } | Select-Object -First 1
        Assert ($null -ne $e)                          "InstantID: $($spec.File) is wired as a Get-Model entry"
        Assert ($e -and $e.Url -match '(?i)\$ix/')     "InstantID: $($spec.File) URL hangs off the `$ix InstantX base"
        $full = ($e.Url -replace '(?i)\$ix', $ixUrl).Trim('"')
        Assert ($full -eq ($ixUrl + $spec.Suffix))     "InstantID: $($spec.File) resolves to the expected InstantX path ($($spec.Suffix))"
    }
    # NOTE (was a "PuLID is deferred" guard): PuLID-Flux is now ALSO wired as its own GATED -Pulid sidecar (the
    # base un-blocked via a NON-GATED FLUX fp8 path — see the dedicated PuLID block below), so FLUX.1-dev/PuLID
    # weights DO now appear in $entries. The InstantID block's OWN weights must still be exactly the 3 InstantX
    # files above (it pulls no FLUX itself) — but that is already pinned by the 3 $ix asserts; a global
    # "no flux anywhere" assertion would now be wrong. The InstantID weights are SDXL-only by construction (they
    # ride $ix = InstantX/InstantID, never a flux/pulid URL), which the per-spec $ix-resolution asserts enforce.
    foreach ($spec in @('ip-adapter.bin', 'instantid_controlnet.safetensors', 'instantid_controlnet_config.json')) {
        $e = $entries | Where-Object { $_.File -eq $spec } | Select-Object -First 1
        Assert ($e -and $e.Url -notmatch '(?i)flux|pulid') "InstantID: $spec is an SDXL InstantX weight, NOT a flux/pulid URL"
    }

    Write-Host "`nPuLID-Flux face-identity (-Pulid): balazik node + a NON-GATED FLUX fp8 base (Kijai unet + ae, comfyanonymous t5xxl/clip_l) + pulid_flux v0.9.1, antelopev2 SHARED with -FaceId"
    # The GATED FLUX face-ID sidecar (-Pulid) un-blocks what InstantID deferred: it clones balazik/ComfyUI-PuLID-Flux
    # (Alpha V0.1.0, last commit 2024-10-03 — node-load is the on-GPU step) and Get-Models a NON-GATED ~17GB FLUX
    # fp8 base (Kijai/flux-fp8 ungated unet + comfyanonymous/flux_text_encoders ungated t5xxl/clip_l + Kijai's ae)
    # plus pulid_flux_v0.9.1 (guozinan/PuLID, ungated). antelopev2 is SHARED with the -FaceId block (same
    # models\insightface\models\antelopev2 path + the same glintr100.onnx sentinel) so it is NOT re-downloaded.
    # Pin: (1) the balazik node is cloned, (2) the fp8 base + encoders + pulid weight are wired Get-Model entries
    # off the verified NON-GATED repos (NOT the gated black-forest-labs / Comfy-Org/flux1-dev repos), with
    # non-colliding local filenames, (3) the antelopev2 fetch is guarded by the glintr100.onnx sentinel so -Pulid
    # does NOT re-fetch it when -FaceId already populated it. NO workflow JSON is registered (the runnable PuLID.json
    # is the on-GPU authoring step — balazik's examples/ are UI-graph). URLs HF-tree-verified this session.
    $puClone = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)balazik/ComfyUI-PuLID-Flux' }, $true)
    Assert ($puClone.Count -ge 1) "PuLID-Flux: setup.ps1 clones the balazik/ComfyUI-PuLID-Flux node (Alpha/stale; node-load is the on-GPU step)"
    # the rejected fork must NOT be cloned.
    $puFork = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)sipie800' }, $true)
    Assert ($puFork.Count -eq 0) "PuLID-Flux: the formally-discontinued sipie800 Enhanced fork is NOT cloned"

    # the FLUX fp8 base + encoders + pulid weight — each a Get-Model entry off a VERIFIED NON-GATED repo.
    foreach ($spec in @(
        @{ File = 'flux1-dev-fp8.safetensors';     Url = '^https://huggingface\.co/Kijai/flux-fp8/resolve/main/flux1-dev-fp8\.safetensors$';                         What = 'fp8 UNET (Kijai, ungated)' },
        @{ File = 't5xxl_fp8_e4m3fn.safetensors';  Url = '^https://huggingface\.co/comfyanonymous/flux_text_encoders/resolve/main/t5xxl_fp8_e4m3fn\.safetensors$';   What = 't5xxl TE (comfyanonymous, ungated)' },
        @{ File = 'clip_l.safetensors';            Url = '^https://huggingface\.co/comfyanonymous/flux_text_encoders/resolve/main/clip_l\.safetensors$';             What = 'clip_l (comfyanonymous, ungated)' },
        @{ File = 'flux-ae.safetensors';           Url = '^https://huggingface\.co/Kijai/flux-fp8/resolve/main/flux-vae-bf16\.safetensors$';                          What = 'FLUX VAE (Kijai, ungated)' },
        @{ File = 'pulid_flux_v0.9.1.safetensors'; Url = '^https://huggingface\.co/guozinan/PuLID/resolve/main/pulid_flux_v0\.9\.1\.safetensors$';                    What = 'PuLID-Flux model (guozinan, ungated)' }
    )) {
        $e = $entries | Where-Object { $_.File -eq $spec.File } | Select-Object -First 1
        Assert ($null -ne $e)               "PuLID-Flux: $($spec.File) is wired as a Get-Model entry ($($spec.What))"
        Assert ($e -and ($e.Url.Trim('"') -match $spec.Url)) "PuLID-Flux: $($spec.File) resolves to the verified NON-GATED URL ($($spec.What))"
    }
    # the base must NOT use the all-in-one dev repos — black-forest-labs/FLUX.1-dev is hard-gated (contact-info
    # license click-through, not scriptable); Comfy-Org/flux1-dev is license-restricted-but-scriptable, but its
    # full-precision all-in-one is a poor 32GB fit, so the fp8 split-files path is the deliberate, conservative choice.
    $gated = $entries | Where-Object { $_.Url -match '(?i)black-forest-labs/FLUX\.1-dev|Comfy-Org/flux1-dev' }
    Assert ($gated.Count -eq 0) "PuLID-Flux: the FLUX base is sourced ONLY from non-gated repos (no black-forest-labs/FLUX.1-dev or Comfy-Org/flux1-dev gated pulls)"

    # antelopev2 SHARED with -FaceId: the -Pulid block must guard its antelopev2 fetch behind the SAME
    # glintr100.onnx sentinel the InstantID block uses, so it does NOT re-download when -FaceId already populated it.
    # AST-pin: the -Pulid block contains an  if (-not (Test-Path $anteOk)) { ... }  guard wrapping the antelopev2
    # Get-Model, where $anteOk = the glintr100.onnx sentinel. Verify the sentinel assignment + the guarded fetch
    # both live inside the  if ($Pulid) { ... }  block.
    $pulidIf = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.IfStatementAst] -and
        $x.Clauses[0].Item1.Extent.Text -match '^\s*\$Pulid\s*$' }, $true) | Select-Object -First 1)
    Assert ($null -ne $pulidIf) "PuLID-Flux: setup.ps1 has an  if (`$Pulid) { ... }  install block"
    $pulidText = if ($pulidIf) { $pulidIf.Extent.Text } else { '' }
    Assert ($pulidText -match '(?i)\$anteOk\s*=\s*Join-Path\s+\$insDir\s+"glintr100\.onnx"') "PuLID-Flux: the antelopev2 sentinel is glintr100.onnx under `$insDir (the SAME insightface path -FaceId uses)"
    Assert ($pulidText -match '(?i)if\s*\(\s*-not\s*\(\s*Test-Path\s+\$anteOk\s*\)\s*\)') "PuLID-Flux: antelopev2 download is GUARDED by  if (-not (Test-Path `$anteOk))  so it is SKIPPED when -FaceId already populated it (no re-download)"
    Assert ($pulidText -match '(?i)models\\insightface\\models\\antelopev2') "PuLID-Flux: antelopev2 lands in the SHARED models\insightface\models\antelopev2 path"
    # the -Pulid block registers NO committed workflow JSON (the runnable PuLID.json is the on-GPU authoring step),
    # so there is intentionally no media-assets\PuLID.json to assert — only the Test-Path-gated copy-or-Warn.
    Assert ($pulidText -match '(?i)Test-Path\s+\$puWf') "PuLID-Flux: the workflow copy is GATED behind Test-Path media-assets\PuLID.json (copy-or-Warn; no blind JSON committed)"

    Write-Host "`nInfiniteTalk audio-driven talking-video (-InfiniteTalk): Kijai WanVideoWrapper node + adapter (`$itk) + wav2vec2 (`$w2v) + the ~82GB Wan2.1-I2V-14B base (`$w21)"
    # The GATED talking-video sidecar (-InfiniteTalk) clones Kijai/ComfyUI-WanVideoWrapper (the REAL ComfyUI
    # integration; the MeiGen `comfyui` branch is itself based on it) and Get-Models THREE weight groups: (A) the
    # InfiniteTalk fp16 ADAPTER off a $itk base (the only new SMALL file; the single-file name carries the REAL
    # upstream "InfiniTetalk" typo), (B) the chinese-wav2vec2-base audio encoder off a $w2v base, and (C) the
    # ~82GB Wan2.1-I2V-14B base off a $w21 base (7 diffusion shards + UMT5-xxl TE + open-clip ViT-H + VAE) — the
    # base DokiDex does NOT otherwise ship (it has Wan 2.2, a different arch the adapter can't inject into). Like
    # InstantID, this block registers NO workflow JSON (no authoritative SwarmUI-API InfiniteTalk.json is
    # sourceable — Kijai's example_workflows are UI-graphs, MeiGen's examples/ are CLI configs), so the runnable
    # media-assets\InfiniteTalk.json is the on-GPU authoring step (see docs/decisions.md). Sizes/URLs HF-tree-verified.
    $wvClone = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)kijai/ComfyUI-WanVideoWrapper' }, $true)
    Assert ($wvClone.Count -ge 1) "InfiniteTalk: setup.ps1 clones the kijai/ComfyUI-WanVideoWrapper node (the real InfiniteTalk/MultiTalk integration, not a standalone MeiGen node)"

    # the three base-URL vars, each pinned to its verified HF resolve/main tree
    $itBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$itk' }, $true) | Select-Object -First 1)
    Assert ($null -ne $itBase) "InfiniteTalk: setup.ps1 defines an `$itk Kijai WanVideo_comfy/InfiniteTalk base URL var"
    $itUrl = if ($itBase) { $itBase.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($itUrl -match '^https://huggingface\.co/Kijai/WanVideo_comfy/resolve/main/InfiniteTalk$') "InfiniteTalk: `$itk = Kijai WanVideo_comfy resolve/main/InfiniteTalk URL"

    $w2vBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$w2v' }, $true) | Select-Object -First 1)
    Assert ($null -ne $w2vBase) "InfiniteTalk: setup.ps1 defines a `$w2v chinese-wav2vec2-base base URL var"
    $w2vUrl = if ($w2vBase) { $w2vBase.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($w2vUrl -match '^https://huggingface\.co/TencentGameMate/chinese-wav2vec2-base/resolve/main$') "InfiniteTalk: `$w2v = TencentGameMate/chinese-wav2vec2-base resolve/main URL"

    $w21Base = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$w21' }, $true) | Select-Object -First 1)
    Assert ($null -ne $w21Base) "InfiniteTalk: setup.ps1 defines a `$w21 Wan2.1-I2V-14B-480P base URL var (the ~82GB base)"
    $w21Url = if ($w21Base) { $w21Base.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($w21Url -match '^https://huggingface\.co/Wan-AI/Wan2\.1-I2V-14B-480P/resolve/main$') "InfiniteTalk: `$w21 = Wan-AI/Wan2.1-I2V-14B-480P resolve/main URL"

    # (A) the adapter — ONLY the fp16 Single (with the REAL upstream "InfiniTetalk" typo) is fetched by default.
    # The default -InfiniteTalk hook is single-portrait, so the ~5.12GB multi-person Multi adapter is a separate
    # MANUAL add (not pulled by the default install) — pin that it is NOT a Get-Model entry so we don't silently
    # re-introduce a 5GB download the single-portrait wiring never uses.
    $itkAdapter = $entries | Where-Object { $_.File -eq 'Wan2_1-InfiniTetalk-Single_fp16.safetensors' } | Select-Object -First 1
    Assert ($null -ne $itkAdapter)                "InfiniteTalk: the fp16 Single adapter is a Get-Model entry"
    Assert ($itkAdapter -and $itkAdapter.Url -match '(?i)\$itk/' -and $itkAdapter.Url -match 'InfiniTetalk') "InfiniteTalk: the Single adapter hangs off `$itk and preserves the real 'InfiniTetalk' upstream typo byte-for-byte"
    $itkMulti = $entries | Where-Object { $_.File -match '(?i)InfiniteTalk-Multi' }
    Assert ($itkMulti.Count -eq 0) "InfiniteTalk: the optional multi-person Multi adapter is NOT fetched by default (separate manual add; the single-portrait path never uses it)"

    # (B) the wav2vec2 encoder bits ride $w2v; the 1.14GB fairseq .pt is intentionally NOT pulled (HF/transformers path)
    foreach ($spec in @(
        @{ File = 'pytorch_model.bin';        Suffix = '/pytorch_model.bin' },
        @{ File = 'config.json';              Suffix = '/config.json' },
        @{ File = 'preprocessor_config.json'; Suffix = '/preprocessor_config.json' }
    )) {
        $e = $entries | Where-Object { $_.File -eq $spec.File -and $_.Url -match '(?i)\$w2v/' } | Select-Object -First 1
        Assert ($null -ne $e)                     "InfiniteTalk: wav2vec2 $($spec.File) is a Get-Model entry off `$w2v"
        $full = ($e.Url -replace '(?i)\$w2v', $w2vUrl).Trim('"')
        Assert ($full -eq ($w2vUrl + $spec.Suffix)) "InfiniteTalk: wav2vec2 $($spec.File) resolves to the expected TencentGameMate path"
    }
    $fairseq = $entries | Where-Object { $_.Url -match '(?i)fairseq' }
    Assert ($fairseq.Count -eq 0) "InfiniteTalk: the 1.14GB fairseq .pt form is NOT pulled (the HF/transformers .bin path is used instead)"

    # (C) the ~82GB Wan2.1 base — 7 diffusion shards off $w21 + the UMT5 TE + open-clip + VAE
    $w21Shards = $entries | Where-Object { $_.Url -match '(?i)\$w21/' -and $_.Url -match 'diffusion_pytorch_model-\d{5}-of-00007\.safetensors' }
    Assert ($w21Shards.Count -eq 7) "InfiniteTalk: the Wan2.1-I2V-14B diffusion base is fetched as all 7 shards off `$w21 (got $($w21Shards.Count))"
    foreach ($spec in @(
        @{ Suffix = '/models_t5_umt5-xxl-enc-bf16.pth';                         Label = 'UMT5-xxl text encoder' },
        @{ Suffix = '/models_clip_open-clip-xlm-roberta-large-vit-huge-14.pth'; Label = 'open-clip ViT-H image encoder' },
        @{ Suffix = '/Wan2.1_VAE.pth';                                          Label = 'Wan2.1 VAE' }
    )) {
        $e = $entries | Where-Object { $_.Url -match '(?i)\$w21/' -and $_.Url.Replace('$w21','').Replace('"','') -eq $spec.Suffix } | Select-Object -First 1
        Assert ($null -ne $e) "InfiniteTalk: the Wan2.1 base $($spec.Label) ($($spec.Suffix)) is a Get-Model entry off `$w21"
    }
    # InfiniteTalk has NO fp8 variant on Kijai's tree (fp16 only) — guard we didn't invent a phantom fp8 adapter URL.
    $fp8Adapter = $entries | Where-Object { $_.Url -match '(?i)InfiniteTalk.*fp8|InfiniTetalk.*fp8' }
    Assert ($fp8Adapter.Count -eq 0) "InfiniteTalk: no phantom fp8 adapter URL (Kijai's tree is fp16-only; an fp8 BASE repack is the on-GPU sourcing step)"

    Write-Host "`nLatentSync LIGHT lip-sync (-LatentSync): ShmuelRonen/ComfyUI-LatentSyncWrapper node + the public ByteDance/LatentSync-1.5 weights (`$ls)"
    # The GATED LIGHT lip-sync (-LatentSync) is the lighter alternative to the shipped ~82GB InfiniteTalk: ByteDance
    # LatentSync 1.5 fits 8GB VRAM and ~9.5GB on disk (roughly 1/9th of InfiniteTalk). Decision-rule branch: install
    # URLs verified, but NO authoritative SwarmUI-API workflow JSON is sourceable (the wrapper's example_workflows
    # are ComfyUI UI-graph exports, not SwarmUI's flat API-prompt CustomWorkflows) -> wire ONLY the gated install +
    # the kind alias + the decisions.md note; the runnable media-assets\LatentSync.json is the on-GPU authoring step
    # (Test-Path Warn, identical to the InfiniteTalk/Foley/InstantID copies). Weights ride the PUBLIC
    # ByteDance/LatentSync-1.5 repo (OpenRAIL++; the 1.6 repo is intermittently gated). Pin: (1) the node is cloned
    # from ShmuelRonen/ComfyUI-LatentSyncWrapper, (2) a $ls base URL var = the LatentSync-1.5 resolve/main tree, (3)
    # the core runtime weights (latentsync_unet.pt + stable_syncnet.pt + whisper/tiny.pt) are wired Get-Model
    # entries with non-colliding local filenames, (4) NO heavy Wan2.1-I2V-14B base is pulled (this is a self-
    # contained SD-VAE-latent model — that would defeat the whole "LIGHT" point). Sizes/URLs HF-API verified.
    $lsClone = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)ShmuelRonen/ComfyUI-LatentSyncWrapper' }, $true)
    Assert ($lsClone.Count -ge 1) "LatentSync: setup.ps1 clones the ShmuelRonen/ComfyUI-LatentSyncWrapper node (the maintained wrapper running current 1.5/1.6 weights)"

    # the base-URL var, pinned to the verified PUBLIC ByteDance/LatentSync-1.5 resolve/main tree (OpenRAIL++)
    $lsBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$ls' }, $true) | Select-Object -First 1)
    Assert ($null -ne $lsBase) "LatentSync: setup.ps1 defines a `$ls ByteDance/LatentSync-1.5 base URL var"
    $lsUrl = if ($lsBase) { $lsBase.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($lsUrl -match '^https://huggingface\.co/ByteDance/LatentSync-1\.5/resolve/main$') "LatentSync: `$ls = ByteDance/LatentSync-1.5 resolve/main URL (the PUBLIC OpenRAIL++ repo; 1.6 is intermittently gated)"

    # the core runtime weights — the diffusion UNet, the SyncNet supervision, and the Whisper-tiny audio encoder
    foreach ($spec in @(
        @{ File = 'latentsync_unet.pt'; Suffix = '/latentsync_unet.pt';   Label = 'diffusion UNet (5.07GB)' },
        @{ File = 'stable_syncnet.pt';  Suffix = '/stable_syncnet.pt';     Label = 'SyncNet supervision (1.61GB)' },
        @{ File = 'tiny.pt';            Suffix = '/whisper/tiny.pt';        Label = 'Whisper-tiny audio encoder (75.6MB)' }
    )) {
        $e = $entries | Where-Object { $_.File -eq $spec.File -and $_.Url -match '(?i)\$ls/' } | Select-Object -First 1
        Assert ($null -ne $e) "LatentSync: the core $($spec.Label) is a Get-Model entry off `$ls"
        $full = ($e.Url -replace '(?i)\$ls', $lsUrl).Trim('"')
        Assert ($full -eq ($lsUrl + $spec.Suffix)) "LatentSync: $($spec.File) resolves to the expected LatentSync-1.5 path ($($spec.Suffix))"
    }
    # the LatentSync-1.5 repo-ROOT config.json (the model config the wrapper loads from checkpoints/config.json) —
    # a Get-Model off $ls, distinct from the SD-VAE's own config below.
    $lsRootCfg = $entries | Where-Object { $_.File -eq 'config.json' -and $_.Url -match '(?i)\$ls/config\.json' } | Select-Object -First 1
    Assert ($null -ne $lsRootCfg) "LatentSync: the repo-root config.json is a Get-Model off `$ls (checkpoints/config.json, the LatentSync-1.5 model config)"

    # THE SD-VAE — REQUIRED. LatentSync is an SD-VAE-LATENT diffusion model: it encodes/decodes frames through
    # stabilityai/sd-vae-ft-mse, so it CANNOT run without it. The wrapper README flags it as a REQUIRED manual
    # download into checkpoints/vae/ (diffusion_pytorch_model.safetensors + config.json). UNGATED (resolve 302s to a
    # public xet CDN, HEAD-verified). Pin: (1) a $sdvae base var = the sd-vae-ft-mse resolve/main tree (a DISTINCT
    # name from the PuLID block's pre-existing $vae DIRECTORY var), (2) BOTH the VAE safetensors weight AND its
    # config.json are Get-Model entries off $sdvae.
    $vaeBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$sdvae' }, $true) | Select-Object -First 1)
    Assert ($null -ne $vaeBase) "LatentSync: setup.ps1 defines a `$sdvae stabilityai/sd-vae-ft-mse base URL var (the REQUIRED SD-VAE)"
    $vaeUrl = if ($vaeBase) { $vaeBase.Right.Extent.Text.Trim('"') } else { '' }
    Assert ($vaeUrl -match '^https://huggingface\.co/stabilityai/sd-vae-ft-mse/resolve/main$') "LatentSync: `$sdvae = stabilityai/sd-vae-ft-mse resolve/main URL (the SD-VAE LatentSync re-encodes frames through; UNGATED)"
    foreach ($spec in @(
        @{ File = 'diffusion_pytorch_model.safetensors'; Suffix = '/diffusion_pytorch_model.safetensors'; Label = 'SD-VAE weights (335MB)' },
        @{ File = 'config.json';                          Suffix = '/config.json';                          Label = 'SD-VAE config (547B)' }
    )) {
        $e = $entries | Where-Object { $_.File -eq $spec.File -and $_.Url -match '(?i)\$sdvae/' } | Select-Object -First 1
        Assert ($null -ne $e) "LatentSync: the $($spec.Label) is a Get-Model entry off `$sdvae (into checkpoints/vae/)"
        $full = ($e.Url -replace '(?i)\$sdvae', $vaeUrl).Trim('"')
        Assert ($full -eq ($vaeUrl + $spec.Suffix)) "LatentSync: $($spec.File) resolves to the expected sd-vae-ft-mse path ($($spec.Suffix))"
    }

    # LatentSync is a self-contained SD-VAE-latent model — it must NOT drag in the heavy Wan2.1-I2V-14B base
    # (that ~82GB base belongs ONLY to InfiniteTalk; pulling it here would defeat the entire "LIGHT" purpose).
    # Its audio encoder is Whisper-tiny, NOT InfiniteTalk's chinese-wav2vec2 — and that base never reuses across.
    $lsWan = $entries | Where-Object { $_.Url -match '(?i)\$ls/' -and $_.Url -match '(?i)Wan2\.?1|I2V-14B|diffusion_pytorch_model-\d{5}' }
    Assert ($lsWan.Count -eq 0) "LatentSync: pulls NO Wan2.1-I2V-14B base off `$ls (it is a self-contained SD-VAE-latent model — the ~82GB base is InfiniteTalk-only; this is the LIGHT pick)"

    Write-Host "`nNunchaku NVFP4 speed runtime (-Nunchaku): nunchaku-ai wheel-from-URL + ComfyUI-nunchaku node + the two nunchaku svdq NVFP4 weights"
    # The GATED speed sidecar (-Nunchaku) installs the nunchaku NVFP4 RUNTIME (a pip wheel + the ComfyUI-nunchaku
    # node), then under -Models full fetches the two nunchaku svdq NVFP4 VARIANTS: Z-Image-Turbo (nunchaku-ai svdq-fp4
    # — the highest-value add, since Z-Image-Turbo is DokiDex's #1 photoreal default / real-time-canvas base, so this
    # DOES accelerate the main path) + Qwen-Image (nunchaku-ai svdq-fp4 base). FLUX.2 Klein NVFP4 is NOT a nunchaku
    # model (it is BFL's OWN native FP4 — see the -Models full block + its own assert below), so it is NOT in this
    # block. Pin: (1) the node is cloned from the nunchaku-tech/ComfyUI-nunchaku repo, (2) the wheel is installed from
    # a nunchaku-tech/nunchaku GitHub RELEASE asset URL (PROBED torch/CUDA/py, not hardcoded — the wheel name carries
    # the cuXX.X + torch + cp tags), (3) the two svdq NVFP4 weights are wired Get-Model entries with the verified
    # resolve URLs + non-colliding local filenames. URLs HF-tree / GitHub-API / HEAD verified this session.
    $nClone = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)nunchaku-tech/ComfyUI-nunchaku' }, $true)
    Assert ($nClone.Count -ge 1) "Nunchaku: setup.ps1 clones the nunchaku-tech/ComfyUI-nunchaku node (the NVFP4 loader/runtime node)"

    # the wheel URL is BUILT (PROBED torch/py) off a nunchaku-tech/nunchaku GitHub release; pin the assembled
    # $wheelUrl assignment targets the releases/download tree (not a hardcoded torch version) + the cu12.8 matrix.
    $wheelAsn = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$wheelUrl' }, $true) | Select-Object -First 1)
    Assert ($null -ne $wheelAsn) "Nunchaku: setup.ps1 assembles a `$wheelUrl for the nunchaku pip wheel"
    $wheelTxt = if ($wheelAsn) { $wheelAsn.Right.Extent.Text } else { '' }
    Assert ($wheelTxt -match '(?i)github\.com/nunchaku-tech/nunchaku/releases/download') "Nunchaku: the wheel URL points at the nunchaku-tech/nunchaku GitHub release-asset tree"
    # the CUDA tag must be PROBED ('$cuTag'), not hardcoded cu12.8 — v1.2.1 ships BOTH cu12.8 (torch 2.8-2.11) and
    # cu13.0 (torch 2.9-2.11) win_amd64 wheels, so a cu13 torch env needs the cu13.0 wheel or import-fails. The URL
    # interpolates a $cuTag built from torch.version.cuda (12.8->cu12.8, 13.0->cu13.0).
    Assert ($wheelTxt -match '\$\{?cuTag\}?')                "Nunchaku: the wheel URL is keyed off the PROBED CUDA build (`$cuTag from torch.version.cuda), not a hardcoded cu12.8"
    # the torch minor must be PROBED ('$tv'), not hardcoded — nunchaku is a compiled ext with no fallback
    Assert ($wheelTxt -match '\$tv')                          "Nunchaku: the wheel URL is keyed off the PROBED torch version (`$tv), not a hardcoded torch minor"
    $wheelProbe = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.Extent.Text -match '(?i)import torch' -and $x.Extent.Text -match '(?i)__version__' }, $true)
    Assert ($wheelProbe.Count -ge 1) "Nunchaku: setup.ps1 PROBES the comfy env's installed torch version (no hardcoded 2.8)"
    # and PROBES torch.version.cuda to choose cu12.8 vs cu13.0 (the compiled ext has no CUDA fallback)
    $cudaProbe = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.Extent.Text -match '(?i)torch\.version\.cuda' }, $true)
    Assert ($cudaProbe.Count -ge 1) "Nunchaku: setup.ps1 PROBES the comfy env's CUDA build (torch.version.cuda) to pick the cu12.8 vs cu13.0 wheel"

    # (the NVFP4 weights — Get-Model entries off the verified resolve URLs)
    # Klein NVFP4: RECLASSIFIED as BFL's OWN NATIVE NVFP4 checkpoint (no svdq- prefix; the model card cites native
    # ComfyUI + Diffusers, NO Nunchaku/SVDQuant; nunchaku's changelog has ZERO FLUX.2 entries). It loads via
    # ComfyUI's native FLUX.2 FP4 path, NOT the ComfyUI-nunchaku node, so it MOVED OUT of the gated -Nunchaku block
    # into the regular -Models full Klein block (near the plain flux-2-klein downloads). $entries is mined globally
    # by AST, so it's still picked up here regardless of which block it lives in. It is the NON-GATED nvfp4 sibling
    # (302->CDN, no 401) — distinct from the 401-gated black-forest-labs/FLUX.2-klein base repo the Comfy-Org
    # repackage exists to avoid; the nvfp4 file is the only place a black-forest-labs resolve URL is permitted.
    $eKleinNv = $entries | Where-Object { $_.File -eq 'flux-2-klein-4b-nvfp4.safetensors' } | Select-Object -First 1
    Assert ($null -ne $eKleinNv) "Klein NVFP4 (native FP4, -Models full): the FLUX.2 Klein 4B NVFP4 weight is a Get-Model entry"
    Assert ($eKleinNv -and $eKleinNv.Url -match '^https://huggingface\.co/black-forest-labs/FLUX\.2-klein-4b-nvfp4/resolve/main/flux-2-klein-4b-nvfp4\.safetensors$') "Klein NVFP4: rides the verified NON-GATED black-forest-labs/FLUX.2-klein-4b-nvfp4 resolve URL"
    # it routes via the existing doki-gen flux-2-klein* glob; no '-base-' infix -> the (conservative, on-GPU-
    # unverified) DISTILLED branch. BFL's nvfp4 card states no inference config, so distilled-vs-base is an assumption.
    Assert ($eKleinNv -and $eKleinNv.File -notmatch '(?i)-base-') "Klein NVFP4: no '-base-' infix -> the existing flux-2-klein* override applies the conservative distilled band (zero recipe change)"

    # Qwen NVFP4: the nunchaku-ai/nunchaku-qwen-image svdq-fp4_r128 NON-Lightning base (matches the base t2i unet)
    $eQwenNv = $entries | Where-Object { $_.File -eq 'svdq-fp4_r128-qwen-image.safetensors' } | Select-Object -First 1
    Assert ($null -ne $eQwenNv) "Nunchaku: the Qwen-Image NVFP4 base weight is a Get-Model entry"
    Assert ($eQwenNv -and $eQwenNv.Url -match '^https://huggingface\.co/nunchaku-ai/nunchaku-qwen-image/resolve/main/svdq-fp4_r128-qwen-image\.safetensors$') "Nunchaku: Qwen NVFP4 rides the verified nunchaku-ai/nunchaku-qwen-image svdq-fp4_r128 resolve URL"
    # it is the NON-Lightning base (so the additive svdq-* override gives it the base 20/4 band, not a low-step cfg=1)
    Assert ($eQwenNv -and $eQwenNv.File -notmatch '(?i)lightning') "Nunchaku: the fetched Qwen NVFP4 file is the NON-Lightning base (the low-step Lightning fp4 distills are a separate on-demand add)"

    # Z-Image-Turbo NVFP4: nunchaku DOES ship a Z-Image-Turbo 4-bit weight (nunchaku-ai/nunchaku-z-image-turbo,
    # added v1.1.0 / perf-boosted v1.2.0) — the single most valuable NVFP4 add since Z-Image-Turbo is DokiDex's
    # #1 photoreal + real-time-canvas BASE. The svdq-fp4_r128 file must be a wired Get-Model entry off the
    # HF-tree/HEAD-verified resolve URL (NOT absent — the earlier "no Z-Image nunchaku arch" claim was WRONG).
    $eZTurboNv = $entries | Where-Object { $_.File -eq 'svdq-fp4_r128-z-image-turbo.safetensors' } | Select-Object -First 1
    Assert ($null -ne $eZTurboNv) "Nunchaku: the Z-Image-Turbo NVFP4 weight IS a wired Get-Model entry (nunchaku v1.1.0 added Z-Image-Turbo 4-bit — the highest-value NVFP4 add for DokiDex's default base)"
    Assert ($eZTurboNv -and $eZTurboNv.Url -match '^https://huggingface\.co/nunchaku-ai/nunchaku-z-image-turbo/resolve/main/svdq-fp4_r128-z-image-turbo\.safetensors$') "Nunchaku: Z-Image-Turbo NVFP4 rides the verified nunchaku-ai/nunchaku-z-image-turbo svdq-fp4_r128 resolve URL"
    # it is the fp4 (Blackwell NVFP4) build, NOT an int4 pre-Blackwell rank variant, and NOT a Lightning distill
    Assert ($eZTurboNv -and $eZTurboNv.File -match '(?i)fp4' -and $eZTurboNv.File -notmatch '(?i)int4|lightning') "Nunchaku: the fetched Z-Image-Turbo file is the svdq-fp4 (Blackwell NVFP4) build, not an int4/Lightning variant"

    Write-Host "`nTTS-Audio-Suite (-TtsSuite): diodiogod node clone; engines AUTO-DOWNLOAD weights on first use (no fragile pre-fetch)"
    # The GATED TTS sidecar (-TtsSuite) is the repo's most-divergent model-add: a 15-engine ComfyUI node that is a
    # GATED ALTERNATIVE to the standalone :8004 Chatterbox server (which stays the coexisting-with-chat default,
    # BYTE-FOR-BYTE untouched). Decision-rule branch: node verified, but the suite's example_workflows are ComfyUI
    # UI-GRAPHS (not SwarmUI API-prompt CustomWorkflows), so this block wires ONLY the gated install (node clone +
    # pip deps); the runnable per-engine TtsSuite-*.json is the on-GPU authoring step (see docs/decisions.md). What
    # -TtsSuite PROVIDES is the node + its python deps; the per-engine weights are NOT pre-fetched — the suite's
    # README states ALL 15 engines AUTO-DOWNLOAD their own models on first node-use. The earlier opt-in pre-fetch of
    # IndexTTS-2 (via `hf`) and Higgs v3 (a lone model.safetensors via Get-Model) was REMOVED: it was two footguns —
    # (a) Higgs was HALF-PINNED (only the weight, not its index.json/config/tokenizer siblings, so a sharded loader
    # could fail to load a lone file) and (b) IndexTTS-2's idempotency gate keyed only on Test-Path gpt.pth, so an
    # interrupted multi-file pull looked "complete" and never resumed. Relying on the node's documented auto-download
    # eliminates both. Pin: (1) the node is cloned from the diodiogod/TTS-Audio-Suite repo; (2) NO IndexTTS-2 `hf`
    # pre-fetch and NO Higgs `$hg`/model.safetensors Get-Model entry remain in the suite block (auto-download owns
    # the weights); (3) no committed TtsSuite-*.json is asserted (on-GPU authoring step).
    $tsClone = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)diodiogod/TTS-Audio-Suite' }, $true)
    Assert ($tsClone.Count -ge 1) "TtsSuite: setup.ps1 clones the diodiogod/TTS-Audio-Suite node (15 TTS engines + RVC)"

    # AUTO-DOWNLOAD CONTRACT — the fragile weight pre-fetches are GONE. Assert the whole setup.ps1 source no longer
    # carries the IndexTTS-2 `hf download IndexTeam/IndexTTS-2` pull, no longer defines the Higgs `$hg` base URL var,
    # and exposes no Get-Model entry for a lone `model.safetensors` off a `$hg` base. (These guard against the
    # half-pinned/partial-pull footguns silently creeping back in.)
    $hfIndex = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'hf' -and
        $x.Extent.Text -match '(?i)IndexTeam/IndexTTS-2' }, $true)
    Assert ($hfIndex.Count -eq 0) "TtsSuite: NO IndexTTS-2 `hf download` pre-fetch remains (the node auto-downloads it on first use; the gpt.pth-only idempotency gate footgun is gone)"
    $hgBase = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $x.Left.Extent.Text -eq '$hg' }, $true) | Select-Object -First 1)
    Assert ($null -eq $hgBase) "TtsSuite: NO `$hg bosonai base URL var remains (the half-pinned Higgs model.safetensors pre-fetch was removed)"
    $eHiggs = $entries | Where-Object { $_.File -eq 'model.safetensors' -and $_.Url -match '(?i)\$hg/' } | Select-Object -First 1
    Assert ($null -eq $eHiggs) "TtsSuite: NO lone Higgs model.safetensors Get-Model entry remains (auto-download fetches the FULL set: weight + index.json + config + tokenizer)"

    # CONTRACT GUARD — the gated alternative must NOT touch the :8004 Chatterbox default. The -TtsSuite block lives
    # entirely in setup.ps1 (a ComfyUI node install); it must NOT re-point/edit the devnen Chatterbox-TTS-Server
    # clone, the :8004 bind, or the /v1/audio/speech path. Assert the suite block introduces no devnen/8004 churn
    # beyond the ONE devnen clone the separate -Tts block already owns (so the coexisting default stays byte-for-byte).
    $devnenClones = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)devnen/Chatterbox' }, $true)
    Assert ($devnenClones.Count -eq 1) "TtsSuite: the :8004 Chatterbox server is cloned exactly ONCE (by -Tts) — -TtsSuite adds NO second/alternate Chatterbox path (the coexisting default stays untouched)"

    Write-Host "`nKokoro fast/light TTS (-Kokoro): remsky/Kokoro-FastAPI node clone — a GATED, ADDITIVE :8006 alternative that does NOT touch the :8004 Chatterbox default"
    # The census DEFER-on-the-default outcome wired the ONE worthwhile gated alternative: Kokoro-82M (Apache-2.0,
    # 82M, <2GB VRAM, CPU-capable, RTF ~0.03) behind remsky/Kokoro-FastAPI — a mature OpenAI-compatible
    # /v1/audio/speech server. It has NO voice cloning (fixed preset voices), so it can NEVER be the default for a
    # custom-voice assistant; it ships strictly as a snappy, near-zero-GPU-contention narration toggle. It mirrors
    # the -Tts Chatterbox block EXACTLY: own venv, loopback-bound, additive — NEW port :8006 (not :8004 Chatterbox
    # / :8005 STT). Pin: (1) the server is git-cloned from the VERIFIED remsky/Kokoro-FastAPI repo into kokoro\,
    # (2) the -Kokoro block does NOT touch the devnen Chatterbox clone / :8004 bind, and (3) the install lives in a
    # dedicated  if ($Kokoro) { ... }  block guarded by its own venv sentinel (resumable, like -Tts).
    $kClone = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)remsky/Kokoro-FastAPI' }, $true)
    Assert ($kClone.Count -ge 1) "Kokoro: setup.ps1 clones the remsky/Kokoro-FastAPI server (the mature OpenAI /v1/audio/speech Kokoro-82M server)"

    $kokoroIf = ($ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.IfStatementAst] -and
        $x.Clauses[0].Item1.Extent.Text -match '^\s*\$Kokoro\s*$' }, $true) | Select-Object -First 1)
    Assert ($null -ne $kokoroIf) "Kokoro: setup.ps1 has an  if (`$Kokoro) { ... }  install block (gated by its own switch)"
    $kokoroText = if ($kokoroIf) { $kokoroIf.Extent.Text } else { '' }
    # additive + loopback + the dedicated :8006 port (not the :8004 Chatterbox or :8005 STT default)
    Assert ($kokoroText -match '8006') "Kokoro: the gated server binds the NEW :8006 port (additive; not the :8004 Chatterbox / :8005 STT default)"
    Assert ($kokoroText -match '(?i)127\.0\.0\.1') "Kokoro: the gated server is loopback-bound (127.0.0.1 — matches every sibling DokiDex server)"
    Assert ($kokoroText -notmatch '(?i)8004') "Kokoro: the -Kokoro block never references :8004 (the coexisting Chatterbox default stays untouched)"
    Assert ($kokoroText -notmatch '(?i)devnen/Chatterbox') "Kokoro: the -Kokoro block does NOT clone/edit the devnen Chatterbox server (additive alternative, never a replacement)"
    # the -Kokoro switch exists as a real param (so the block is reachable + gated)
    $kokoroParam = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.ParameterAst] -and
        $x.Name.VariablePath.UserPath -eq 'Kokoro' }, $true)
    Assert ($kokoroParam.Count -ge 1) "Kokoro: setup.ps1 declares a -Kokoro switch parameter"
    # the install is resumable behind a venv .deps-ok sentinel (same discipline as -Tts)
    Assert ($kokoroText -match '(?i)\.deps-ok') "Kokoro: the venv install is gated by a .deps-ok sentinel (resumable, like -Tts)"
    # install-time WEIGHT pre-fetch: Kokoro-FastAPI does NOT lazy-download on first request (unlike Chatterbox's
    # voice model), so the -Kokoro block must run the repo's own download_model.py at install. Pin: (1) it invokes
    # docker\scripts\download_model.py, (2) into the api\src\models\v1_0 --output dir the launcher's MODEL_DIR reads
    # (model_dir=src\models -> api\src\models, loader appends the v1_0/ prefix from pytorch_kokoro_v1_file), (3) it is
    # idempotent (Test-Path skip on the .pth) and (4) Warn-on-failure (a download hiccup must not abort the block).
    Assert ($kokoroText -match '(?i)docker\\scripts\\download_model\.py') "Kokoro: the block runs the repo's docker\scripts\download_model.py to pre-fetch the Kokoro-82M weights at install (no lazy first-request download)"
    Assert ($kokoroText -match '(?i)api\\src\\models\\v1_0') "Kokoro: the weights download into api\src\models\v1_0 (the dir the launcher's MODEL_DIR=src\models + the v1_0/ filename prefix resolve to)"
    Assert ($kokoroText -match '(?i)--output') "Kokoro: download_model.py is passed --output (the required arg; the script has no default output dir)"
    Assert ($kokoroText -match '(?i)kokoro-v1_0\.pth') "Kokoro: the download is idempotent — guarded on the kokoro-v1_0.pth weight existing (skip on re-run)"
    Assert ($kokoroText -match '(?i)Warn\b.*(?i)(download|weight)') "Kokoro: a weight-download failure WARNS (does not abort the -Kokoro block — the venv is already provisioned; the weight is retryable)"

    # CONTRACT GUARD (whole-file): -Kokoro must NOT add a second devnen/Chatterbox clone — :8004 stays cloned
    # exactly once (by -Tts). (The -TtsSuite block already asserts the same; re-pin here so wiring Kokoro can't
    # regress it.)
    $devnenAll = $ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.CommandAst] -and
        $x.GetCommandName() -eq 'Git-Clone' -and
        $x.Extent.Text -match '(?i)devnen/Chatterbox' }, $true)
    Assert ($devnenAll.Count -eq 1) "Kokoro: the :8004 Chatterbox server is still cloned exactly ONCE (by -Tts) — -Kokoro adds no second/alternate Chatterbox path"

    # every Get-Model lands a UNIQUE destination (dir + filename) — a true duplicate dest would make one model
    # silently shadow another. Keyed on the full (Join-Path $dir "<file>") expression, NOT the bare filename, so a
    # filename reused in a DIFFERENT subdir (e.g. checkpoints/config.json vs checkpoints/vae/config.json, both
    # required by the LatentSync wrapper) is correctly allowed; only a same-dir same-name collision fails.
    $dupes = $entries | Where-Object { $_.Dest } | Group-Object Dest | Where-Object { $_.Count -gt 1 }
    Assert ($dupes.Count -eq 0) "no two Get-Model entries collide on the same destination (dir + filename)$(if ($dupes) { ' (dupes: ' + (($dupes | ForEach-Object { $_.Name }) -join ', ') + ')' })"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
$color = if ($script:fail) { "Red" } else { "Green" }
Write-Host ("setup-helpers: {0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $color
exit ([int]($script:fail -gt 0))
