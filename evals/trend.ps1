# Fold the append-only eval log into pass-rate history (GPU-free, pure reshape).
# run-suite.ps1 writes one-shot dated scorecards but never trends them; this
# rolls every results.jsonl row up two ways: per (model x harness) and per task.
# Usage: .\trend.ps1                         # console tables, all rows
#        .\trend.ps1 -Model coder-fast       # only that model
#        .\trend.ps1 -Write                  # also emit docs\scorecards\TRENDS.md
param(
    [string]$Path = (Join-Path $PSScriptRoot "results.jsonl"),
    [string]$Model,
    [string]$Harness,
    [switch]$Write
)
$ErrorActionPreference = "Stop"
$evals = $PSScriptRoot

if (-not (Test-Path $Path)) { throw "no results log at $Path (run run-suite.ps1 first)" }

# Each line is one ConvertTo-Json -Compress scorecard row:
#   {ts, harness, model, task, pass(bool), seconds, note}
# Skip blanks so a trailing newline doesn't blow up ConvertFrom-Json.
$rows = @(Get-Content $Path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
if ($Model) { $rows = @($rows | Where-Object { $_.model -eq $Model }) }
if ($Harness) { $rows = @($rows | Where-Object { $_.harness -eq $Harness }) }
if (-not $rows.Count) { Write-Host "no rows match (model=$Model harness=$Harness)"; exit 0 }

# Roll one group of rows into a pass-rate + most-recent snapshot. Newest row by
# ts wins 'last'; ts is sortable 's' format so string compare is chronological.
function Get-Summary($group) {
    $runs = $group.Count
    $passed = @($group | Where-Object pass).Count
    $rate = [int][math]::Round(100.0 * $passed / $runs, 0)
    $recent = $group | Sort-Object ts | Select-Object -Last 1
    # ConvertFrom-Json parsed ts to [datetime]; sort stays chronological, but
    # render back to the log's sortable 's' string (compact, not locale M/d/yyyy).
    $when = if ($recent.ts -is [datetime]) { Get-Date $recent.ts -Format s } else { "$($recent.ts)" }
    [pscustomobject]@{
        Runs = $runs; Passed = $passed; Rate = $rate
        Last = if ($recent.pass) { "PASS" } else { "fail" }; When = $when
    }
}

# 1. Per model x harness (overall reliability of each contender)
$byModel = foreach ($g in ($rows | Group-Object model, harness | Sort-Object Name)) {
    $s = Get-Summary $g.Group
    [pscustomobject]@{
        Model = $g.Group[0].model; Harness = $g.Group[0].harness
        Runs = $s.Runs; Passed = $s.Passed; "Rate%" = $s.Rate; Last = $s.Last; When = $s.When
    }
}

# 2. Per task (which golden tasks are flaky / consistently failing)
$byTask = foreach ($g in ($rows | Group-Object task | Sort-Object Name)) {
    $s = Get-Summary $g.Group
    [pscustomobject]@{
        Task = $g.Name
        Runs = $s.Runs; Passed = $s.Passed; "Rate%" = $s.Rate; Last = $s.Last; When = $s.When
    }
}

$scope = if ($Model -or $Harness) { " (model=$Model harness=$Harness)" } else { "" }
Write-Host ""
Write-Host "TREND  $($rows.Count) results across $((@($rows | Group-Object task).Count)) tasks$scope"
Write-Host ""
Write-Host "By model x harness:"
$byModel | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "By task:"
$byTask | Format-Table -AutoSize | Out-String | Write-Host

if ($Write) {
    $cardDir = Join-Path (Split-Path $evals) "docs\scorecards"
    New-Item -ItemType Directory -Force $cardDir | Out-Null
    $md = Join-Path $cardDir "TRENDS.md"
    $lines = @(
        "# Eval trends — $(Get-Date -Format 'yyyy-MM-dd')",
        "",
        "Folded from ``evals/results.jsonl`` ($($rows.Count) results).$scope",
        "",
        "## By model x harness",
        "",
        "| Model | Harness | Runs | Passed | Rate | Last | When |",
        "|---|---|---|---|---|---|---|"
    )
    foreach ($r in $byModel) {
        $mark = if ($r.Last -eq "PASS") { "✅" } else { "❌" }
        $lines += "| $($r.Model) | $($r.Harness) | $($r.Runs) | $($r.Passed) | $($r."Rate%")% | $mark | $($r.When) |"
    }
    $lines += @("", "## By task", "", "| Task | Runs | Passed | Rate | Last | When |", "|---|---|---|---|---|---|")
    foreach ($r in $byTask) {
        $mark = if ($r.Last -eq "PASS") { "✅" } else { "❌" }
        $lines += "| $($r.Task) | $($r.Runs) | $($r.Passed) | $($r."Rate%")% | $mark | $($r.When) |"
    }
    $lines | Set-Content $md
    Write-Host "wrote $md"
}
exit 0
