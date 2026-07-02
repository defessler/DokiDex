# Run the full golden-task suite for one harness × model and write a scorecard.
# Usage: .\run-suite.ps1 -Harness crush -Model coder-fast
param(
    [Parameter(Mandatory)][ValidateSet("crush", "opencode", "claw")][string]$Harness,
    [Parameter(Mandatory)][string]$Model,
    [int]$TimeoutSec = 540,
    # AUDIT quick-win #8 (2026-07-01): gate enforcement is opt-in (default 0 = no gate, matching prior
    # behavior) so ad-hoc suite runs are unaffected. Bake-off / promotion callers pass e.g.
    # -MinPassRate 91 to make the ">=91% golden" policy documented in docs/decisions.md an enforced,
    # scriptable gate instead of a manually-eyeballed number.
    [double]$MinPassRate = 0
)
$ErrorActionPreference = "Stop"
$evals = $PSScriptRoot
$tasks = Get-ChildItem (Join-Path $evals "tasks") -Directory | Sort-Object Name
$results = @()

foreach ($t in $tasks) {
    Write-Host ">>> $($t.Name)"
    & (Join-Path $evals "run-eval.ps1") -Harness $Harness -Model $Model -Task $t.Name -TimeoutSec $TimeoutSec
    $last = Get-Content (Join-Path $evals "results.jsonl") -Tail 1 | ConvertFrom-Json
    $results += $last
}

$passed = @($results | Where-Object pass).Count
$total = $results.Count
$rate = [math]::Round(100.0 * $passed / $total, 0)

$dateTag = Get-Date -Format "yyyy-MM-dd"
$cardDir = Join-Path (Split-Path $evals) "docs\scorecards"
New-Item -ItemType Directory -Force $cardDir | Out-Null
$card = Join-Path $cardDir "$dateTag-$Harness-$Model.md"

$lines = @(
    "# Scorecard — $Harness x $Model — $dateTag",
    "",
    "**$passed / $total passed ($rate%)**",
    "",
    "| Task | Pass | Seconds | Note |",
    "|---|---|---|---|"
)
foreach ($r in $results) {
    $mark = if ($r.pass) { "✅" } else { "❌" }
    $lines += "| $($r.task) | $mark | $($r.seconds) | $($r.note) |"
}
$lines | Set-Content $card

Write-Host ""
Write-Host "SUITE COMPLETE  $Harness x $Model  $passed/$total ($rate%)  -> $card"

if ($MinPassRate -gt 0 -and $rate -lt $MinPassRate) {
    Write-Error "GATE FAIL: $rate% < required $MinPassRate%"
    exit 1
}

exit 0
