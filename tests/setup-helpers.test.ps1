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
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
$color = if ($script:fail) { "Red" } else { "Green" }
Write-Host ("setup-helpers: {0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $color
exit ([int]($script:fail -gt 0))
