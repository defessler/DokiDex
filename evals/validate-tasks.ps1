# Task-design validator: every task's check must FAIL against the unsolved
# start state (seed + inject). A task that passes unsolved is broken.
$ErrorActionPreference = "Stop"
$evals = $PSScriptRoot
$bad = 0
foreach ($taskDir in Get-ChildItem (Join-Path $evals "tasks") -Directory) {
    $work = Join-Path $env:TEMP ("doki-validate-" + $taskDir.Name)
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    robocopy (Join-Path $evals "sandbox-seed") $work /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    $inject = Join-Path $taskDir.FullName "inject"
    if (Test-Path $inject) { robocopy $inject $work /E /NFL /NDL /NJH /NJS /NP | Out-Null }

    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $taskDir.FullName "check.ps1") -Work $work *> $null
    $passedUnsolved = ($LASTEXITCODE -eq 0)
    if ($passedUnsolved) { Write-Host "BROKEN  $($taskDir.Name): check passes with no agent work"; $bad++ }
    else { Write-Host "OK      $($taskDir.Name): correctly fails unsolved" }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($bad) { Write-Host "$bad broken task(s)"; exit 1 }
Write-Host "ALL TASKS VALID (fail when unsolved)"
exit 0
