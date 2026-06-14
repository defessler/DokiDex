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
    [Parameter(Position = 0)][ValidateSet("up", "down", "status", "restart", "logs", "verify", "start", "stop", "panel")][string]$Command = "status",
    [Parameter(Position = 1)][string]$Arg
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$serving = Join-Path $root "serving"
$runDir = Join-Path $root ".run"
New-Item -ItemType Directory -Force $runDir | Out-Null

$Services = [ordered]@{
    "llama-swap" = @{ script = (Join-Path $serving "start-serving.ps1"); health = "http://127.0.0.1:8080/v1/models"; group = "llm";   desc = "agent inference :8080"; port = 8080; ui = "http://127.0.0.1:8080/ui"; vramGB = 26 }
    "fim"        = @{ script = (Join-Path $serving "start-fim.ps1");     health = "http://127.0.0.1:8012/health";   group = "llm";   desc = "autocomplete  :8012"; port = 8012; ui = $null; vramGB = 4 }
    # Uncensored TTS (:8004) — OpenAI-compatible /v1/audio/speech + voice cloning. ~4GB, group=llm
    # so it coexists with the coder in agent mode. 'requires' skips it cleanly when not installed.
    "tts"        = @{ script = (Join-Path $serving "start-tts.ps1");     health = "http://127.0.0.1:8004/";         group = "llm";   desc = "speech/TTS    :8004"; port = 8004; ui = "http://127.0.0.1:8004/"; vramGB = 4; requires = (Join-Path $root "tts\Chatterbox-TTS-Server\.venv\Scripts\python.exe") }
    "media"      = @{ script = (Join-Path $serving "start-media.ps1");   health = "http://127.0.0.1:7801/";         group = "media"; desc = "image+video   :7801"; port = 7801; ui = "http://127.0.0.1:7801/"; vramGB = 18 }
    # Tiny always-on prompt-rewriter (:8013) — auto-expands lazy prompts for SwarmUI.
    # group=media so it coexists with the image/video model (and is stopped for the big LLM).
    # 'requires' lets doki skip it cleanly on lean installs where its model isn't present.
    "prompt-rewriter" = @{ script = (Join-Path $serving "start-prompt-rewriter.ps1"); health = "http://127.0.0.1:8013/health"; group = "media"; desc = "prompt rewriter :8013"; port = 8013; ui = $null; vramGB = 3; requires = (Join-Path $root "models\Qwen2.5-3B-Instruct-Q5_K_M.gguf") }
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
function GpuJson {
    # one nvidia-smi call -> the panel's VRAM gauge. Per-process VRAM is [N/A] on this
    # WDDM driver, so we report the whole card attributed to the active group (honest).
    try {
        $raw = & nvidia-smi --query-gpu=memory.used,memory.total,utilization.gpu,temperature.gpu,power.draw,fan.speed '--format=csv,noheader,nounits' 2>$null
        $line = @($raw)[0]
        if ($line) {
            $p = $line -split ',' | ForEach-Object { $_.Trim() }
            $active = "none"
            foreach ($n in $Services.Keys) { if ((IsRunning $n)) { $active = $Services[$n].group; break } }
            $fan = if ($p.Count -gt 5 -and $p[5] -match '^\d') { [int][double]$p[5] } else { $null }
            return [pscustomobject]@{
                usedMB = [int][double]$p[0]; totalMB = [int][double]$p[1]
                util = [int][double]$p[2]; temp = [int][double]$p[3]
                watts = [double]$p[4]; fan = $fan
                perProcess = $false; activeGroup = $active
            }
        }
    } catch {}
    $null
}
function LlamaSwapModel {
    # loaded model + configured menu from llama-swap; all best-effort (null on a cold swap).
    $info = [pscustomobject]@{ model = $null; modelState = $null; configuredModels = @() }
    try {
        $r = Invoke-RestMethod "http://127.0.0.1:8080/running" -TimeoutSec 2 -ErrorAction Stop
        $first = if ($r.running) { @($r.running)[0] } else { $r }
        if ($first) { $info.model = $first.model; $info.modelState = $first.state }
    } catch {}
    try {
        $m = Invoke-RestMethod "http://127.0.0.1:8080/v1/models" -TimeoutSec 2 -ErrorAction Stop
        $info.configuredModels = @($m.data | ForEach-Object { $_.id })
    } catch {}
    $info
}
function StatusJson {
    # machine-readable status for the control panel — one source of truth from $Services.
    $arr = foreach ($n in $Services.Keys) {
        $s = $Services[$n]
        $port = if ($s.port) { [int]$s.port } else { $m = [regex]::Match([string]$s.health, ':(\d+)'); if ($m.Success) { [int]$m.Groups[1].Value } else { $null } }
        $procId = $null
        $pf = PidFile $n
        if (Test-Path $pf) { $procId = (Get-Content $pf -ErrorAction SilentlyContinue) }
        $model = $null; $modelState = $null; $configured = @()
        if ($n -eq 'llama-swap' -and (Probe $s.health)) { $li = LlamaSwapModel; $model = $li.model; $modelState = $li.modelState; $configured = $li.configuredModels }
        [pscustomobject]@{
            name      = $n
            group     = $s.group
            desc      = $s.desc
            port      = $port
            ui        = $s.ui
            vramGB    = $s.vramGB
            health    = $s.health
            healthy   = (Probe $s.health)
            running   = (IsRunning $n)
            pid       = if ($procId) { [int]$procId } else { $null }
            installed = ((-not $s.requires) -or (Test-Path $s.requires))
            model            = $model
            modelState       = $modelState
            configuredModels = @($configured)
            version   = ""   # filled by the panel's update job
            update    = ""
            profiles  = @($Profiles.Keys | Where-Object { $Profiles[$_] -contains $n })
        }
    }
    @{ services = @($arr); profiles = $Profiles; gpu = (GpuJson) } | ConvertTo-Json -Depth 5
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
function RestartArg($a) {
    # restart <service> restarts just that service; restart [profile] restarts the whole profile.
    if ($a -and $Services.Contains($a)) { StopSvc $a; StartOne $a }
    else { DoDown; DoUp $a }
}
function LaunchPanel {
    $exe = Join-Path $root "control\bin\Release\net9.0-windows\DokiCode.Control.exe"
    $proj = Join-Path $root "control\DokiCode.Control.csproj"
    if (Test-Path $exe) { Start-Process $exe }
    elseif (Test-Path $proj) { Write-Host "launching control panel (dev) ..."; Start-Process "dotnet" -ArgumentList @("run", "--project", "`"$proj`"", "-c", "Release") }
    else { Write-Host "control panel not built. Build it with:  dotnet build control\DokiCode.Control.csproj -c Release" }
}

switch ($Command) {
    "up"      { DoUp $Arg }
    "down"    { DoDown }
    "restart" { RestartArg $Arg }
    "start"   { if (-not $Arg) { throw "usage: .\doki.ps1 start <service>" }; StartOne $Arg }
    "stop"    { if (-not $Arg) { throw "usage: .\doki.ps1 stop <service>" }; StopOne $Arg }
    "panel"   { LaunchPanel }
    "status"  { if ($Arg -eq "json" -or $Arg -eq "--json") { StatusJson } else { ShowStatus } }
    "logs" {
        if (-not $Arg) { throw "usage: .\doki.ps1 logs <llama-swap|fim|media>" }
        $l = LogFile $Arg
        if (Test-Path $l) { Get-Content $l -Tail 40 -Wait } else { Write-Host "no log for '$Arg' yet" }
    }
    "verify" { & (Join-Path $root "verify.ps1") }
}
