# tests/verify-gated.test.ps1 — the `doki verify -Gated` gated-integration registry + pure status logic.
#
# `doki verify -Gated` extends verify.ps1 to report, for EVERY gated integration (the 10 setup.ps1 -Flag
# sidecars: -Sam/-Demucs/-Train/-FaceId/-InfiniteTalk/-LatentSync/-Pulid/-Nunchaku/-TtsSuite/-Kokoro), what is
# checkable WITHOUT a GPU — the node clone dir + the weight files on disk — and PRINTS the on-GPU TODO that
# decisions.md records. Multi-GB weights are gitignored + ABSENT in CI, so the checks must DEGRADE to
# 'not installed', NEVER fail the harness; the live node-load/render stays SKIP-by-default.
#
# This pins these contracts:
#   (a) COVERAGE  — the gated-flag set is DERIVED FROM SOURCE (every [switch] param whose positive
#                   `if ($Flag){}` block runs an install action — Git-Clone/Get-Model/custom_nodes/pip —
#                   minus an explicit always-on allow-list), and the registry covers it both directions.
#                   A genuinely NEW gated -Flag with no registry row FAILS — the no-silent-skip guarantee.
#   (b) PURE LOGIC — Get-GatedStatus(entry, exists-predicate) maps all-present->ready, none->not installed,
#                   some->partial, present-but-no-workflow->on-GPU TODO. Stubbed predicate, no disk, no GPU.
#   (c) ROOTING   — each entry's Files resolve against the CORRECT base: VenvRoot sidecars (Demucs/Sam/Train)
#                   root their Files at $root; NodeDir entries root at $swarm (incl. Nunchaku's own Models\ tree).
#   (d) NEVER THROWS — with EVERY file absent (the CI reality) the status logic degrades cleanly.
#
# Framework-free: exit 0 = all pass, 1 = fail. Mirrors tests/setup-helpers.test.ps1. Run via `doki test`.

$ErrorActionPreference = "Stop"
$root     = Split-Path $PSScriptRoot -Parent
$setup    = Join-Path $root "setup.ps1"
$registry = Join-Path $PSScriptRoot "gated-registry.ps1"
if (-not (Test-Path $setup))    { Write-Error "setup.ps1 not found at $setup"; exit 2 }
if (-not (Test-Path $registry)) { Write-Error "gated-registry.ps1 not found at $registry"; exit 2 }

# --- assertions ---
$script:pass = 0; $script:fail = 0
function Assert($cond, $msg) {
    if ($cond) { $script:pass++; Write-Host "  [PASS] $msg" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
}

# --- load the COMMITTED registry (the same file verify.ps1 -Gated dot-sources; zero duplication) ---
# It must expose $GatedRegistry (ordered map name->entry) and Get-GatedStatus (the pure classifier).
. $registry
Assert ($null -ne $GatedRegistry)                 "gated-registry.ps1 exposes a `$GatedRegistry map"
Assert ($GatedRegistry -is [System.Collections.IDictionary]) "`$GatedRegistry is a dictionary (name -> entry)"
Assert ((Get-Command Get-GatedStatus -ErrorAction SilentlyContinue) -ne $null) "gated-registry.ps1 exposes the pure Get-GatedStatus function"

Write-Host "`nCOVERAGE — the registry covers every gated -Flag in setup.ps1 (source-DERIVED, both directions)"
# The harness's CORE PROMISE: a genuinely NEW gated integration ([switch]$Foo + `if ($Foo){ Git-Clone / Get-Model }`)
# must NOT be able to ship unverified. A hardcoded flag-name regex can't see a name it doesn't already list, so we
# DERIVE the gated-flag set FROM SOURCE: every [switch] param whose positive `if ($Flag){...}` block performs an
# INSTALL action (Git-Clone / Get-Model / a custom_nodes clone / pip), MINUS an explicit always-on allow-list (the
# always-on stack the default verify smokes already cover — Vision/Tts/Stt/Managed/LlmCandidates). So a new gated
# -Flag that isn't registered AND isn't in the allow-list fails the coverage assertion below.
#
# ALWAYS-ON ALLOW-LIST — these [switch] params ALSO run installs (Tts: Git-Clone+pip; Stt: pip; Vision/LlmCandidates:
# model fetch; Managed: none) but are the default, always-verified stack, NOT gated sidecars. They are EXCLUDED by
# name, on purpose, with this comment as the audit trail. (Media has no positive `if ($Media){}` install block — it
# gates via `if (-not $Media){ return }`, an early-return guard — so the positive-clause miner never matches it.)
$AlwaysOnFlags = @('Vision', 'Tts', 'Stt', 'Managed', 'LlmCandidates')

function Get-MinedGatedFlags {
    # DERIVE the gated-install flags from a setup AST: [switch] params whose positive `if ($Flag){...}` block
    # contains an install action, minus the always-on allow-list. Pure (AST in -> name[] out), so it runs against
    # both the real setup.ps1 AND an inline fixture (the new-unregistered-flag proof below).
    param(
        [Parameter(Mandatory)] $Ast,
        [string[]] $AllowList = @()
    )
    # 1. all [switch] parameter names (the universe a gated flag can be drawn from)
    $switchParams = $Ast.FindAll({ param($x)
        $x -is [System.Management.Automation.Language.ParameterAst] -and
        ($x.Attributes | Where-Object { $_ -is [System.Management.Automation.Language.TypeConstraintAst] -and $_.TypeName.Name -eq 'switch' })
    }, $true) | ForEach-Object { $_.Name.VariablePath.UserPath }
    $switchSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$switchParams, [System.StringComparer]::OrdinalIgnoreCase)

    # 2. positive `if ($Flag){...}` blocks (bare-variable clause — NOT `-not $Flag`, NOT a comparison) whose Flag is
    #    a [switch] param AND whose block body performs an install action.
    $installRx = '(?i)(Git-Clone|Get-Model|custom_nodes|-m\s+pip\s+install|Pip\s+\$)'
    $mined = $Ast.FindAll({ param($x) $x -is [System.Management.Automation.Language.IfStatementAst] }, $true) | ForEach-Object {
        $cond = $_.Clauses[0].Item1
        # the clause must be a BARE variable reference ($Flag), so `-not $Media` / `$Models -eq 'full'` never match
        $pipe = $cond.PipelineElements
        $bare = $pipe.Count -eq 1 -and $pipe[0].Expression -is [System.Management.Automation.Language.VariableExpressionAst]
        if ($bare) {
            $name = $pipe[0].Expression.VariablePath.UserPath
            if ($switchSet.Contains($name) -and ($_.Extent.Text -match $installRx)) { $name }
        }
    } | Select-Object -Unique
    # 3. drop the always-on allow-list (matched case-insensitively)
    $allow = [System.Collections.Generic.HashSet[string]]::new([string[]]$AllowList, [System.StringComparer]::OrdinalIgnoreCase)
    @($mined | Where-Object { -not $allow.Contains($_) })
}

$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $setup).Path, [ref]$null, [ref]$null)
$gatedFlags = Get-MinedGatedFlags -Ast $ast -AllowList $AlwaysOnFlags
Assert ($gatedFlags.Count -eq 10) "setup.ps1 source-derives EXACTLY the 10 gated -Flag install blocks ($($gatedFlags.Count) found: $(($gatedFlags | Sort-Object) -join ', '))"
# the always-on installers must be EXCLUDED (they're the default-verify stack, not gated sidecars)
foreach ($a in @('Tts', 'Stt', 'Vision', 'LlmCandidates', 'Managed', 'Media')) {
    Assert ($gatedFlags -notcontains $a) "coverage: always-on -$a is NOT mined as a gated sidecar (correctly excluded from the gated set)"
}

$registryFlags = $GatedRegistry.Values.Flag
# (1) every gated -Flag in setup has a registry entry — the no-silent-skip guard
foreach ($f in $gatedFlags) {
    Assert ($registryFlags -contains $f) "coverage: gated -$f has a registry entry (a new gated -Flag can't skip verification)"
}
# (2) every registry .Flag is a real gated switch in setup (no stale row)
foreach ($f in $registryFlags) {
    Assert ($gatedFlags -contains $f) "coverage: registry -$f maps to a real  if (`$$f){}  block in setup.ps1 (no stale entry)"
}

Write-Host "`nMINER PROOF — a NEW gated -Flag with no registry row MUST be caught (the no-silent-skip guarantee)"
# The exact failure the harness claims to prevent: someone adds a real gated integration to setup.ps1 and forgets
# the registry row. Prove the source-derived miner catches it. We parse an INLINE fixture that is structurally a
# setup.ps1 — a [switch]$Foo param + an `if ($Foo){ Git-Clone... / Get-Model... }` install block (NOT in the
# allow-list) — and confirm the miner SURFACES $Foo. Then we show the registry has no 'Foo' row, so the coverage
# assertion above (`$registryFlags -contains $f`) would be FALSE for it => the test FAILS. (Asserted positively
# here so the proof itself is a passing, regression-protected check rather than a transient red.)
$fixtureSrc = @'
param(
    [switch]$Foo,        # a NEW gated integration the author forgot to register
    [switch]$Quiet,      # a non-install switch (flag-gated behavior, NOT a sidecar) — must NOT be mined
    [switch]$Vision,     # an always-on-style installer that IS in the allow-list — must NOT be mined
    [switch]$Media
)
if (-not $Media) { return }                                  # early-return guard: never a positive install block
if ($Foo)   { Git-Clone https://example/Foo $dir; Get-Model "https://example/foo.safetensors" $w }
if ($Quiet) { Write-Host "quiet mode" }                      # no install action -> not gated
if ($Vision){ Get-Model "https://example/vision.gguf" $v }   # installs, but allow-listed -> excluded
'@
$fixtureAst = [System.Management.Automation.Language.Parser]::ParseInput($fixtureSrc, [ref]$null, [ref]$null)
$fixtureMined = Get-MinedGatedFlags -Ast $fixtureAst -AllowList $AlwaysOnFlags
Assert ($fixtureMined -contains 'Foo') "miner: a NEW `if (`$Foo){ Git-Clone/Get-Model }` install block IS source-derived as gated ($($fixtureMined -join ', '))"
Assert ($fixtureMined -notcontains 'Quiet') "miner: a flag-gated NON-install switch (-Quiet) is NOT mined (no Git-Clone/Get-Model/pip)"
Assert ($fixtureMined -notcontains 'Vision') "miner: an allow-listed installer (-Vision) is excluded even though it installs"
Assert ($fixtureMined -notcontains 'Media') "miner: an early-return guard (`if (-not `$Media)`) is never mined as a positive install block"
# the clincher: an UNREGISTERED mined flag fails the no-silent-skip guard. Prove the coverage predicate is FALSE
# for $Foo (it has no registry row) — i.e. had $Foo been real in setup.ps1, the COVERAGE assertion would have failed.
Assert (-not ($registryFlags -contains 'Foo')) "miner-proof: the unregistered mined flag -Foo has NO registry row, so the coverage guard would FAIL it (a new gated -Flag cannot ship unverified)"

Write-Host "`nREGISTRY SHAPE — each entry carries what -Gated needs to check + print"
foreach ($name in $GatedRegistry.Keys) {
    $e = $GatedRegistry[$name]
    Assert ($e.Flag)  "shape: '$name' declares its -Flag"
    Assert ($e.OnGpu) "shape: '$name' carries the on-GPU TODO runbook string"
    # a sidecar either clones a node dir (NodeDir) or installs a venv (VenvRoot) — at least one locates it on disk
    Assert ($e.NodeDir -or $e.VenvRoot) "shape: '$name' locates its install on disk (NodeDir or VenvRoot)"
    Assert ($e.ContainsKey('Files')) "shape: '$name' declares its weight Files (possibly empty for pip-only sidecars)"
}

Write-Host "`nSTATUS ENUM + LABELING — the producer/consumer token table, and the render-gate discriminator"
# (c) the status enum is the SINGLE source of truth both Get-GatedStatus and verify.ps1's PASS/SKIP switch use.
Assert ($null -ne $GatedStatus) "gated-registry.ps1 exposes the `$GatedStatus token table (the producer/consumer enum)"
Assert ($GatedStatus.Ready -eq 'ready' -and $GatedStatus.Partial -eq 'partial' -and $GatedStatus.NotInstalled -eq 'not installed' -and $GatedStatus.WorkflowTodo -eq 'installed; workflow is on-GPU TODO') "the `$GatedStatus tokens carry the exact status values the classifier returns + the grid prints"
# (a) the verify.ps1 'ready' PASS line branches on whether the entry has a labeled on-GPU RENDER step: the venv
# sidecars (Demucs/Sam/Train) record 'no labeled on-GPU step' in their OnGpu, so they must NOT be told a render is
# still TODO; the render-gated integrations must be. Pin that discriminator (the exact predicate verify.ps1 uses:
# OnGpu -notmatch 'no labeled on-GPU step') so an OnGpu edit can't silently flip a label.
$noRenderFlags = @('Demucs', 'Sam', 'Train')   # the venv sidecars with no on-GPU render gate
foreach ($name in $GatedRegistry.Keys) {
    $e = $GatedRegistry[$name]
    $renderGated = $e.OnGpu -notmatch 'no labeled on-GPU step'
    if ($noRenderFlags -contains $e.Flag) {
        Assert (-not $renderGated) "labeling: -$($e.Flag) is a no-render venv sidecar (OnGpu says 'no labeled on-GPU step') -> its ready PASS must NOT claim a render is on-GPU TODO"
    } else {
        Assert ($renderGated) "labeling: -$($e.Flag) is render-gated (OnGpu describes an on-GPU render/author step) -> its ready PASS DOES carry 'live render is on-GPU TODO'"
    }
}

Write-Host "`nPURE STATUS LOGIC — Get-GatedStatus(entry, exists-predicate); no disk, no GPU, deterministic"
# A table-backed fake predicate: a path 'exists' iff it's in $present. This lets us drive every branch with
# multi-GB files declared absent — exactly the CI reality — and assert the pure mapping.
function New-Exists([string[]]$present) {
    $set = [System.Collections.Generic.HashSet[string]]::new([string[]]$present, [System.StringComparer]::OrdinalIgnoreCase)
    return { param($p) $set.Contains("$p") }.GetNewClosure()
}
# pick a representative entry that has a node dir + weights + a workflow (InfiniteTalk: node + many weights + JSON)
$sample = $GatedRegistry.Values | Where-Object { $_.NodeDir -and $_.Files.Count -gt 0 -and $_.Workflow } | Select-Object -First 1
Assert ($null -ne $sample) "a node+weights+workflow entry exists to exercise every status branch"
if ($sample) {
    $swarm = Join-Path $root "media\SwarmUI"
    $nodeP = Join-Path $swarm $sample.NodeDir
    $weightPs = $sample.Files | ForEach-Object { Join-Path $swarm $_ }
    $wfP   = Join-Path $root $sample.Workflow

    # none present -> 'not installed'
    $s = Get-GatedStatus $sample (New-Exists @())
    Assert ($s -eq 'not installed') "status: nothing on disk -> 'not installed' (got '$s')"

    # node present, NO weights -> 'partial'
    $s = Get-GatedStatus $sample (New-Exists @($nodeP))
    Assert ($s -eq 'partial') "status: node dir but no weights -> 'partial' (got '$s')"

    # node + SOME weights -> 'partial'
    $s = Get-GatedStatus $sample (New-Exists (@($nodeP) + @($weightPs[0])))
    Assert ($s -eq 'partial') "status: node + only some weights -> 'partial' (got '$s')"

    # node + ALL weights, NO workflow JSON -> the on-GPU TODO state
    $s = Get-GatedStatus $sample (New-Exists (@($nodeP) + $weightPs))
    Assert ($s -eq 'installed; workflow is on-GPU TODO') "status: node+all weights, no workflow -> on-GPU TODO (got '$s')"

    # everything present incl. the workflow -> 'ready'
    $s = Get-GatedStatus $sample (New-Exists (@($nodeP) + $weightPs + @($wfP)))
    Assert ($s -eq 'ready') "status: node + all weights + workflow -> 'ready' (got '$s')"
}

# a workflow-less sidecar (Nunchaku/Sam/Demucs/Train: Workflow=$null) reaches 'ready' with just node+weights
$noWf = $GatedRegistry.Values | Where-Object { -not $_.Workflow } | Select-Object -First 1
Assert ($null -ne $noWf) "a workflow-less sidecar exists (pip-only / no JSON)"
if ($noWf) {
    $swarm = Join-Path $root "media\SwarmUI"
    $loc   = if ($noWf.NodeDir) { Join-Path $swarm $noWf.NodeDir } else { Join-Path $root $noWf.VenvRoot }
    $wbase = if ($noWf.VenvRoot) { $root } else { $swarm }   # VenvRoot Files root at $root; NodeDir Files at $swarm
    $wps   = $noWf.Files | ForEach-Object { Join-Path $wbase $_ }
    $s = Get-GatedStatus $noWf (New-Exists (@($loc) + $wps))
    Assert ($s -eq 'ready') "status: a Workflow=`$null sidecar is 'ready' on node+weights alone (no JSON gate) (got '$s')"
}

Write-Host "`nROOTING — each entry's Files resolve against the CORRECT base (VenvRoot->`$root, NodeDir->`$swarm)"
# These pin the two rooting traps the generic samples above can't catch (the workflow-less sample is a NodeDir
# entry, so it never exercises VenvRoot+Files, nor the Models\-vs-dlbackend\models tree). A RECORDING predicate
# captures every path Get-GatedStatus actually probes, so we assert the JOINED path string — not just the leaf.
$swarm = [System.IO.Path]::GetFullPath((Join-Path $root "media\SwarmUI"))
$rootF = [System.IO.Path]::GetFullPath($root)
function New-Recorder {
    # like New-Exists but also records (case-insensitively, normalized to full paths) every probed path so a test
    # can assert the EXACT base each Files entry was joined to. Returns @{ exists=<scriptblock>; seen=<HashSet> }.
    param([string[]]$present)
    $set  = [System.Collections.Generic.HashSet[string]]::new([string[]]$present, [System.StringComparer]::OrdinalIgnoreCase)
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $pred = {
        param($p)
        $full = [System.IO.Path]::GetFullPath("$p")   # collapse ..\ so the recorded string is the canonical path
        [void]$seen.Add($full)
        $set.Contains($full) -or $set.Contains("$p")
    }.GetNewClosure()
    return @{ exists = $pred; seen = $seen }
}

# (a) FIX 1 — a VenvRoot entry WITH a Files weight (SAM) must root its Files at $root, NOT $swarm.
#     Today's bug joins EVERY Files entry to $swarm, so an installed SAM reports 'partial' forever.
$sam = $GatedRegistry.Values | Where-Object { $_.Flag -eq 'Sam' } | Select-Object -First 1
Assert ($null -ne $sam) "SAM entry present (the VenvRoot+Files sidecar that pins the rooting fix)"
Assert ($sam -and $sam.VenvRoot -and $sam.Files.Count -gt 0) "SAM is the VenvRoot entry that ALSO declares a weight file (the only one of its kind)"
if ($sam -and $sam.VenvRoot -and $sam.Files.Count) {
    $samVenv   = [System.IO.Path]::GetFullPath((Join-Path $rootF $sam.VenvRoot))            # audio-tools\sam\.venv  under $root
    $samWeight = [System.IO.Path]::GetFullPath((Join-Path $rootF $sam.Files[0]))            # audio-tools\sam\sam_vit_b.pth  under $root (CORRECT)
    $samWrong  = [System.IO.Path]::GetFullPath((Join-Path $swarm $sam.Files[0]))            # the BUGGY media\SwarmUI\... rooting
    # the present set declares the venv + the weight at their REAL ($root) locations:
    $rec = New-Recorder @($samVenv, $samWeight)
    $s = Get-GatedStatus $sam $rec.exists
    Assert ($s -eq 'ready') "rooting: SAM (VenvRoot + present weight at `$root\$($sam.Files[0])) classifies 'ready'/installed (got '$s') [FIX 1]"
    Assert ($rec.seen.Contains($samWeight)) "rooting: SAM probes its weight under `$root ($samWeight), proving Files root at `$root not `$swarm"
    Assert (-not $rec.seen.Contains($samWrong)) "rooting: SAM does NOT probe the wrong media\SwarmUI rooting ($samWrong)"
}

# (b) the Nunchaku Models\-tree rooting (NodeDir entry): its svdq weights live under SwarmUI's OWN Models\ tree
#     ($smodels), NOT the raw ComfyUI dlbackend\...\models tree ($cmodels). Guards the just-fixed must-fix.
$nun = $GatedRegistry.Values | Where-Object { $_.Flag -eq 'Nunchaku' } | Select-Object -First 1
Assert ($null -ne $nun) "Nunchaku entry present (the Models\-tree NodeDir sidecar)"
if ($nun -and $nun.NodeDir -and $nun.Files.Count) {
    $nunNode    = [System.IO.Path]::GetFullPath((Join-Path $swarm $nun.NodeDir))            # custom_nodes\ComfyUI-nunchaku under $swarm
    $nunWeight  = [System.IO.Path]::GetFullPath((Join-Path $swarm $nun.Files[0]))           # Models\diffusion_models\svdq-... under $swarm (CORRECT)
    $nunWeights = $nun.Files | ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $swarm $_)) }  # ALL weights present -> 'ready'
    $nunRaw     = [System.IO.Path]::GetFullPath((Join-Path $swarm "dlbackend\comfy\ComfyUI\models\diffusion_models\$(Split-Path $nun.Files[0] -Leaf)"))  # the WRONG raw-tree rooting
    Assert ($nun.Files[0] -match '^Models\\') "rooting: Nunchaku weight is declared under SwarmUI's OWN Models\ tree, not dlbackend\...\models ($($nun.Files[0]))"
    $rec = New-Recorder (@($nunNode) + $nunWeights)
    $s = Get-GatedStatus $nun $rec.exists
    Assert ($s -eq 'ready') "rooting: Nunchaku (NodeDir + present weight under `$swarm\$($nun.Files[0])) classifies 'ready' (got '$s')"
    Assert ($rec.seen.Contains($nunWeight)) "rooting: Nunchaku probes its weight under `$swarm\Models\ ($nunWeight), not the raw ComfyUI models tree"
    Assert (-not $rec.seen.Contains($nunRaw)) "rooting: Nunchaku does NOT probe the wrong dlbackend\...\models rooting ($nunRaw) [must-fix regression guard]"
}

Write-Host "`nNEVER THROWS — the CI reality: EVERY gated artifact absent must degrade, not fault"
$everAbsent = { param($p) $false }   # nothing on disk, ever
foreach ($name in $GatedRegistry.Keys) {
    $threw = $false; $st = $null
    try { $st = Get-GatedStatus $GatedRegistry[$name] $everAbsent } catch { $threw = $true }
    Assert (-not $threw) "no-throw: '$name' classifies cleanly with all files absent"
    Assert ($st -eq 'not installed') "degrade: '$name' with all files absent -> 'not installed' (got '$st')"
}

Write-Host "`nWEIGHT/NODE drift guard — every registry node + weight is grounded in the matching setup.ps1 block"
# Cross-check the registry against the AST: each entry's Flag has an  if ($Flag){}  block whose extent text
# mentions the registry's node-dir leaf AND every weight filename — so the registry can't drift from the
# actual install (a renamed weight in setup with a stale registry row fails here).
# Index the install block for each SOURCE-DERIVED gated flag (the same $gatedFlags the coverage guard mined), so a
# new gated -Flag is drift-checked too — no hardcoded name list to fall out of sync with setup.ps1.
$gatedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$gatedFlags, [System.StringComparer]::OrdinalIgnoreCase)
$ifBlocks = @{}
foreach ($b in $ast.FindAll({ param($x) $x -is [System.Management.Automation.Language.IfStatementAst] }, $true)) {
    $cond = $b.Clauses[0].Item1
    $pipe = $cond.PipelineElements
    if ($pipe.Count -eq 1 -and $pipe[0].Expression -is [System.Management.Automation.Language.VariableExpressionAst]) {
        $fname = $pipe[0].Expression.VariablePath.UserPath
        if ($gatedSet.Contains($fname)) { $ifBlocks[$fname] = $b.Extent.Text }
    }
}
foreach ($name in $GatedRegistry.Keys) {
    $e = $GatedRegistry[$name]
    $txt = $ifBlocks[$e.Flag]
    if (-not $txt) { continue }   # coverage test above already flags a missing block
    if ($e.NodeDir) {
        $leaf = Split-Path $e.NodeDir -Leaf
        Assert ($txt -match [regex]::Escape($leaf)) "drift: '$name' node dir '$leaf' appears in the  if (`$$($e.Flag)){}  block"
    }
    foreach ($w in $e.Files) {
        $wleaf = Split-Path $w -Leaf
        Assert ($txt -match [regex]::Escape($wleaf)) "drift: '$name' weight '$wleaf' is fetched in the  if (`$$($e.Flag)){}  block"
        # ...and the wrong-TREE guard: the leaf alone passed even when the registry pointed at the wrong parent dir
        # (the Nunchaku $cmodels-vs-$smodels bug). Also require the immediate parent segment to appear in the block,
        # so a path rooted at the wrong tree (different parent dir) is caught instead of matching on the leaf alone.
        $parent = Split-Path (Split-Path $w -Parent) -Leaf
        if ($parent) {
            Assert ($txt -match [regex]::Escape($parent)) "drift: '$name' weight '$wleaf' parent dir '$parent' appears in the  if (`$$($e.Flag)){}  block (wrong-tree guard)"
        }
    }
}

Write-Host ""
$color = if ($script:fail) { "Red" } else { "Green" }
Write-Host ("verify-gated: {0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $color
exit ([int]($script:fail -gt 0))
