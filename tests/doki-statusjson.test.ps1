# tests/doki-statusjson.test.ps1 — contract test for `doki status json`, the seam the WPF
# control panel parses. Runs the real command in a child pwsh (works whether or not services
# are up) and validates the schema + cross-consistency: every field the panel reads is present,
# every profile names only real services, every group is llm|media. Catches drift that would
# otherwise break the panel silently. Framework-free: exit 0 = pass, 1 = fail.

$ErrorActionPreference = "Stop"
$doki = Join-Path $PSScriptRoot "..\doki.ps1"
if (-not (Test-Path $doki)) { Write-Error "doki.ps1 not found at $doki"; exit 2 }

$script:pass = 0; $script:fail = 0
function Assert($cond, $msg) {
    if ($cond) { $script:pass++; Write-Host "  [PASS] $msg" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
}

# Run the real command; stderr suppressed so only the JSON document lands in $raw.
$raw = & pwsh -NoProfile -File $doki status json 2>$null
$obj = $null
try { $obj = ($raw -join "`n") | ConvertFrom-Json } catch { }

Assert ($null -ne $obj) "status json emits a parseable JSON document"
if ($null -eq $obj) {
    Write-Host "`ndoki-statusjson: $script:pass passed, $script:fail failed" -ForegroundColor Red
    exit 1
}

# top-level shape
Assert ($null -ne $obj.services)                                  "top level has 'services'"
Assert ($obj.PSObject.Properties.Name -contains 'profiles')       "top level has 'profiles'"
Assert ($obj.PSObject.Properties.Name -contains 'gpu')            "top level has 'gpu' (null ok when no nvidia-smi)"

$svc = @($obj.services)
Assert ($svc.Count -ge 1) "services is a non-empty array ($($svc.Count) services)"

# every field the panel's ParseStatus consumes must be present, per service
$required = @('name', 'group', 'desc', 'port', 'ui', 'vramGB', 'health', 'healthy',
    'running', 'pid', 'installed', 'model', 'modelState', 'configuredModels', 'profiles')
foreach ($s in $svc) {
    $have = $s.PSObject.Properties.Name
    $missing = @($required | Where-Object { $have -notcontains $_ })
    Assert ($missing.Count -eq 0) ("service '{0}' has all panel fields{1}" -f $s.name, $(if ($missing) { " — MISSING: $($missing -join ',')" } else { "" }))
    Assert ($s.group -in @('llm', 'media')) "service '$($s.name)' group is llm|media (got '$($s.group)')"
    Assert ($s.healthy -is [bool])          "service '$($s.name)' .healthy is boolean"
    Assert ($s.running -is [bool])          "service '$($s.name)' .running is boolean"
}

# cross-consistency: every profile member is a real service name (catches typos / renames)
$names = @($svc.name)
foreach ($p in $obj.profiles.PSObject.Properties) {
    foreach ($member in @($p.Value)) {
        Assert ($names -contains $member) "profile '$($p.Name)' references real service '$member'"
    }
}
# the three documented profiles must exist
foreach ($p in @('agent', 'coexist', 'media')) {
    Assert ($obj.profiles.PSObject.Properties.Name -contains $p) "profile '$p' present"
}

# llm vs media are the two GPU-exclusive groups; both must be represented in the registry
$groups = @($svc.group | Select-Object -Unique)
Assert ($groups -contains 'llm')   "registry has at least one llm-group service"
Assert ($groups -contains 'media') "registry has at least one media-group service"

Write-Host ""
$color = if ($script:fail) { "Red" } else { "Green" }
Write-Host ("doki-statusjson: {0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $color
exit ([int]($script:fail -gt 0))
