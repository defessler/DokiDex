# Run the full golden-task suite for one harness × model and write a scorecard.
# Usage: .\run-suite.ps1 -Harness crush -Model coder-fast
param(
    [Parameter(Mandatory)][ValidateSet("crush", "opencode", "claw")][string]$Harness,
    [Parameter(Mandatory)][string]$Model,
    [int]$TimeoutSec = 540
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
exit 0
