# doki.ps1 — DokiCode native control plane (docker-compose-style, no Docker).
#
#   .\doki.ps1 up [agent|coexist|media]   start a profile detached (default: agent)
#   .\doki.ps1 down                        stop all managed services
#   .\doki.ps1 status                      show services + health
#   .\doki.ps1 restart [profile]           down, then up
#   .\doki.ps1 logs <llama-swap|fim|media> tail a service log
#
# GPU modes are mutually exclusive on 32GB: agent/coexist (LLM) vs media (image/
# video). 'up media' stops the LLM servers first; 'up agent|coexist' stops media.
param(
    [Parameter(Position = 0)][ValidateSet("up", "down", "status", "restart", "logs", "verify", "start", "stop")][string]$Command = "status",
    [Parameter(Position = 1)][string]$Arg
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$serving = Join-Path $root "serving"
$runDir = Join-Path $root ".run"
New-Item -ItemType Directory -Force $runDir | Out-Null

$Services = [ordered]@{
    "llama-swap" = @{ script = (Join-Path $serving "start-serving.ps1"); health = "http://127.0.0.1:8080/v1/models"; group = "llm";   desc = "agent inference :8080" }
    "fim"        = @{ script = (Join-Path $serving "start-fim.ps1");     health = "http://127.0.0.1:8012/health";   group = "llm";   desc = "autocomplete  :8012" }
    # Uncensored TTS (:8004) — OpenAI-compatible /v1/audio/speech + voice cloning. ~4GB, group=llm
    # so it coexists with the coder in agent mode. 'requires' skips it cleanly when not installed.
    "tts"        = @{ script = (Join-Path $serving "start-tts.ps1");     health = "http://127.0.0.1:8004/";         group = "llm";   desc = "speech/TTS    :8004"; requires = (Join-Path $root "tts\Chatterbox-TTS-Server\.venv\Scripts\python.exe") }
    "media"      = @{ script = (Join-Path $serving "start-media.ps1");   health = "http://127.0.0.1:7801/";         group = "media"; desc = "image+video   :7801" }
    # Tiny always-on prompt-rewriter (:8013) — auto-expands lazy prompts for SwarmUI.
    # group=media so it coexists with the image/video model (and is stopped for the big LLM).
    # 'requires' lets doki skip it cleanly on lean installs where its model isn't present.
    "prompt-rewriter" = @{ script = (Join-Path $serving "start-prompt-rewriter.ps1"); health = "http://127.0.0.1:8013/health"; group = "media"; desc = "prompt rewriter :8013"; requires = (Join-Path $root "models\Qwen2.5-3B-Instruct-Q5_K_M.gguf") }
}
$Profiles = [ordered]@{ agent = @("llama-swap", "tts"); coexist = @("llama-swap", "fim"); media = @("media", "prompt-rewriter") }

function PidFile($n) { Join-Path $runDir "$n.pid" }
function LogFile($n) { Join-Path $runDir "$n.log" }
function IsRunning($n) {
    $pf = PidFile $n
    if (-not (Test-Path $pf)) { return $false }
    $procId = Get-Content $pf -ErrorAction SilentlyContinue
    if (-not $procId) { return $false }
    [bool](Get-Process -Id $procId -ErrorAction SilentlyContinue)
}
function Probe($u) { try { Invoke-WebRequest $u -TimeoutSec 3 -UseBasicParsing | Out-Null; $true } catch { $false } }
function StartSvc($n) {
    if (IsRunning $n) { Write-Host "  $n already up"; return }
    if (Probe $Services[$n].health) { Write-Host "  $n already up (untracked — started outside doki)"; return }
    & $Services[$n].script -Detach -PidFile (PidFile $n) -LogFile (LogFile $n) | Out-Null
    Write-Host "  + $n  $($Services[$n].desc)"
}
function StopSvc($n) {
    $pf = PidFile $n
    if (Test-Path $pf) {
        $procId = Get-Content $pf -ErrorAction SilentlyContinue
        if ($procId) { taskkill /PID $procId /T /F *> $null }
        Remove-Item $pf -Force -ErrorAction SilentlyContinue
        Write-Host "  - $n stopped"
    }
}
function WaitHealth($n, $timeout = 120) {
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt $timeout) {
        if (Probe $Services[$n].health) { return $true }
        Start-Sleep -Milliseconds 800
    }
    $false
}
function ShowStatus {
    Write-Host ""
    Write-Host "DokiCode services"
    Write-Host "-----------------"
    foreach ($n in $Services.Keys) {
        $h = Probe $Services[$n].health
        $run = IsRunning $n
        $state = if ($h) { "UP  (healthy)" } elseif ($run) { "UP  (starting)" } else { "down" }
        "{0,-12} {1,-22} {2}" -f $n, $Services[$n].desc, $state | Write-Host
    }
    Write-Host ""
}
function StatusJson {
    # machine-readable status for the control panel — one source of truth from $Services.
    $arr = foreach ($n in $Services.Keys) {
        $s = $Services[$n]
        $port = ([regex]::Match([string]$s.health, ':(\d+)')).Groups[1].Value
        [pscustomobject]@{
            name      = $n
            group     = $s.group
            desc      = $s.desc
            port      = if ($port) { [int]$port } else { $null }
            health    = $s.health
            healthy   = (Probe $s.health)
            running   = (IsRunning $n)
            installed = ((-not $s.requires) -or (Test-Path $s.requires))
            profiles  = @($Profiles.Keys | Where-Object { $Profiles[$_] -contains $n })
        }
    }
    @{ services = @($arr); profiles = $Profiles } | ConvertTo-Json -Depth 5
}
function DoUp($profile) {
    if (-not $profile) { $profile = "agent" }
    if (-not $Profiles.Contains($profile)) { throw "unknown profile '$profile' — use: agent | coexist | media" }
    $wantGroup = if ($profile -eq "media") { "media" } else { "llm" }
    foreach ($n in $Services.Keys) { if ($Services[$n].group -ne $wantGroup -and (IsRunning $n)) { StopSvc $n } }
    Write-Host "doki up [$profile]"
    $starting = @()
    foreach ($n in $Profiles[$profile]) {
        if ($Services[$n].requires -and -not (Test-Path $Services[$n].requires)) { Write-Host "  ~ $n skipped (not installed — run setup.ps1 -Media -Models full)"; continue }
        StartSvc $n
        $starting += $n
    }
    foreach ($n in $starting) {
        if (WaitHealth $n) { Write-Host "  ok  $n healthy" } else { Write-Host "  ..  $n started; health not confirmed yet (first model load can be slow)" }
    }
    ShowStatus
}
function DoDown { Write-Host "doki down"; foreach ($n in $Services.Keys) { StopSvc $n } }
function StartOne($name) {
    if (-not $Services.Contains($name)) { throw "unknown service '$name' — one of: $($Services.Keys -join ', ')" }
    if ($Services[$name].requires -and -not (Test-Path $Services[$name].requires)) { Write-Host "$name not installed"; return }
    # respect the 32GB GPU mutual-exclusion: stop the opposite group first
    $g = $Services[$name].group
    foreach ($n in $Services.Keys) { if ($Services[$n].group -ne $g -and (IsRunning $n)) { StopSvc $n } }
    StartSvc $name
    if (WaitHealth $name) { Write-Host "  ok  $name healthy" } else { Write-Host "  ..  $name started; health not confirmed yet" }
    ShowStatus
}
function StopOne($name) {
    if (-not $Services.Contains($name)) { throw "unknown service '$name' — one of: $($Services.Keys -join ', ')" }
    StopSvc $name
    ShowStatus
}

switch ($Command) {
    "up"      { DoUp $Arg }
    "down"    { DoDown }
    "restart" { DoDown; DoUp $Arg }
    "start"   { if (-not $Arg) { throw "usage: .\doki.ps1 start <service>" }; StartOne $Arg }
    "stop"    { if (-not $Arg) { throw "usage: .\doki.ps1 stop <service>" }; StopOne $Arg }
    "status"  { if ($Arg -eq "json" -or $Arg -eq "--json") { StatusJson } else { ShowStatus } }
    "logs" {
        if (-not $Arg) { throw "usage: .\doki.ps1 logs <llama-swap|fim|media>" }
        $l = LogFile $Arg
        if (Test-Path $l) { Get-Content $l -Tail 40 -Wait } else { Write-Host "no log for '$Arg' yet" }
    }
    "verify" { & (Join-Path $root "verify.ps1") }
}
