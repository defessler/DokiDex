# DokiCode golden-task eval runner.
# Usage: .\run-eval.ps1 -Harness crush -Model coder-fast -Task t2-slugify-bug
# Each run: fresh copy of sandbox-seed -> task inject -> headless harness run
# -> objective check -> append to results.jsonl.
param(
    [Parameter(Mandatory)][ValidateSet("crush", "opencode")][string]$Harness,
    [Parameter(Mandatory)][string]$Model,
    [Parameter(Mandatory)][string]$Task,
    [int]$TimeoutSec = 540,
    [switch]$KeepWork
)
$ErrorActionPreference = "Stop"
$evals = $PSScriptRoot
$taskDir = Join-Path $evals "tasks\$Task"
$taskDef = Get-Content (Join-Path $taskDir "task.json") -Raw | ConvertFrom-Json

# Fresh workspace from seed
$work = Join-Path $env:TEMP ("doki-eval-" + [guid]::NewGuid().ToString("n").Substring(0, 8))
robocopy (Join-Path $evals "sandbox-seed") $work /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy seed failed: $LASTEXITCODE" }
$inject = Join-Path $taskDir "inject"
if (Test-Path $inject) {
    robocopy $inject $work /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy inject failed: $LASTEXITCODE" }
}

# Harness invocation (headless)
$log = Join-Path $work "_agent-output.log"
$errlog = Join-Path $work "_agent-error.log"
if ($Harness -eq "crush") {
    # Per-workspace permissions allowlist (headless runs can't answer prompts).
    # Provider comes from the global ~/.config/crush/crush.json; stays merged.
    @'
{
  "$schema": "https://charm.land/crush.json",
  "permissions": {
    "allowed_tools": ["view", "ls", "grep", "glob", "edit", "multiedit", "write", "bash", "download", "fetch", "diagnostics"]
  }
}
'@ | Set-Content (Join-Path $work "crush.json")
    $exe = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\charmbracelet.crush_Microsoft.Winget.Source_8wekyb3d8bbwe\crush_0.76.0_Windows_x86_64\crush.exe"
    # '--' terminates flag parsing: prompt text containing --tokens must not
    # be eaten by the CLI parser (Start-Process joins args UNQUOTED).
    $argList = @("run", "-q", "-m", "local/$Model", "--", $taskDef.prompt)
} else {
    # Native exe, NOT the npm .cmd shim: cmd.exe would interpret <, >, & in prompts.
    $exe = "$env:APPDATA\npm\node_modules\opencode-ai\bin\opencode.exe"
    $argList = @("run", "-m", "local/$Model", "--", $taskDef.prompt)
}

# CRITICAL: crush/opencode 'run' accept piped stdin; a never-closing pipe hangs
# them forever. Redirect stdin from an empty file so they see immediate EOF.
$stdinFile = Join-Path $work "_stdin.empty"
New-Item -ItemType File -Path $stdinFile -Force | Out-Null

$t0 = Get-Date
$p = Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $work -NoNewWindow -PassThru `
    -RedirectStandardInput $stdinFile -RedirectStandardOutput $log -RedirectStandardError $errlog
$timedOut = -not $p.WaitForExit($TimeoutSec * 1000)
if ($timedOut) { $p.Kill($true) }
$seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)

# Objective check (isolated process so check's exit code is clean)
$pass = $false; $note = "timeout"
if (-not $timedOut) {
    $checkOut = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $taskDir "check.ps1") -Work $work 2>&1
    $pass = ($LASTEXITCODE -eq 0)
    $note = ($checkOut | Select-Object -Last 1)
}

# Record
$result = [ordered]@{
    ts = (Get-Date -Format s); harness = $Harness; model = $Model; task = $Task
    pass = $pass; seconds = $seconds; note = "$note"
}
($result | ConvertTo-Json -Compress) | Add-Content (Join-Path $evals "results.jsonl")
Write-Host ("RESULT  {0} x {1} x {2}  pass={3}  {4}s  note={5}" -f $Harness, $Model, $Task, $pass, $seconds, $note)
if ($KeepWork) { Write-Host "work kept at $work" } else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
exit 0
