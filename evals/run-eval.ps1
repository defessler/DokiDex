# DokiCode golden-task eval runner.
# Usage: .\run-eval.ps1 -Harness crush -Model coder-fast -Task t2-slugify-bug
# Each run: fresh copy of sandbox-seed -> task inject -> headless harness run
# -> objective check -> append to results.jsonl.
param(
    [Parameter(Mandatory)][ValidateSet("crush", "opencode", "claw")][string]$Harness,
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
} elseif ($Harness -eq "opencode") {
    # Native exe, NOT the npm .cmd shim: cmd.exe would interpret <, >, & in prompts.
    $exe = "$env:APPDATA\npm\node_modules\opencode-ai\bin\opencode.exe"
    $argList = @("run", "-m", "local/$Model", "--", $taskDef.prompt)
} else {
    # Claw Code - clean-room Rust reimpl of the Claude Code harness, via the
    # codetwentyfive/claw-code-local fork that wires in OpenAI-compatible providers.
    # Build with evals\build-claw.ps1 (needs Rust); override path with $env:CLAW_EXE.
    $exe = if ($env:CLAW_EXE) {
        $env:CLAW_EXE
    } elseif (Test-Path "$env:LOCALAPPDATA\claw-code-local\rust\target\release\claw.exe") {
        "$env:LOCALAPPDATA\claw-code-local\rust\target\release\claw.exe"
    } elseif (Get-Command claw -ErrorAction SilentlyContinue) {
        (Get-Command claw).Source
    } else {
        throw 'claw.exe not found - run evals\build-claw.ps1 first (or set $env:CLAW_EXE)'
    }

    # claw reads CLAUDE.md, not AGENTS.md. Mirror the seed's conventions so the
    # bake-off stays apples-to-apples (same rules, different harness).
    $agentsFile = Join-Path $work "AGENTS.md"
    if (Test-Path $agentsFile) { Copy-Item $agentsFile (Join-Path $work "CLAUDE.md") -Force }

    # Provider routing (api/src/providers/mod.rs detect_provider_kind): with no
    # Anthropic auth present and OPENAI_API_KEY set, a non-claude/grok model name
    # routes to the OpenAI-compatible client -> our llama-swap endpoint.
    $env:OPENAI_BASE_URL = "http://127.0.0.1:8080/v1"
    $env:OPENAI_API_KEY = "dummy"
    $savedAnthropic = $env:ANTHROPIC_API_KEY
    Remove-Item Env:\ANTHROPIC_API_KEY -ErrorAction SilentlyContinue

    # 'prompt' = non-interactive one-shot; '--' guards dash-leading prompt text;
    # danger-full-access auto-runs tools (safe: throwaway workspace, like the
    # other harnesses in headless mode).
    $argList = @("--model", $Model, "--permission-mode", "danger-full-access", "prompt", "--", $taskDef.prompt)
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
if ($Harness -eq "claw" -and $savedAnthropic) { $env:ANTHROPIC_API_KEY = $savedAnthropic }

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
