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
    $d2 = Join-Path $work "got2.bin"
    $badUrl = ([Uri](Join-Path $work "does-not-exist.bin")).AbsoluteUri
    Get-Model $badUrl $d2
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
        # the dest is a (Join-Path $dir "<file>") paren-expression; grab its last string constant = the filename
        $paren = $c.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.ParenExpressionAst] } | Select-Object -First 1
        $file  = $null
        if ($paren) { $file = ($paren.FindAll({ param($y) $y -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | Select-Object -Last 1).Value }
        [pscustomobject]@{ Url = $url; File = $file }
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
    # PuLID-Flux guard: the picked SDXL path must NEVER pull FLUX.1-dev (the rejected ~22GB base) or a PuLID weight.
    $fluxPull = $entries | Where-Object { $_.Url -match '(?i)flux\.?1-dev|pulid' }
    Assert ($fluxPull.Count -eq 0) "InstantID: the SDXL face-ID path pulls NO FLUX.1-dev / PuLID weights (PuLID-Flux is deferred)"

    # every Get-Model lands a UNIQUE local filename (a duplicate would make one model silently shadow another)
    $dupes = $entries | Where-Object { $_.File } | Group-Object File | Where-Object { $_.Count -gt 1 }
    Assert ($dupes.Count -eq 0) "no two Get-Model entries collide on the same local filename$(if ($dupes) { ' (dupes: ' + (($dupes | ForEach-Object { $_.Name }) -join ', ') + ')' })"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
$color = if ($script:fail) { "Red" } else { "Green" }
Write-Host ("setup-helpers: {0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $color
exit ([int]($script:fail -gt 0))
