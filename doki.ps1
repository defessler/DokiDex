# doki.ps1 — DokiDex native control plane (docker-compose-style, no Docker).
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
    [Parameter(Position = 0)][ValidateSet("up", "down", "status", "restart", "logs", "verify", "start", "stop", "panel", "test", "doctor")][string]$Command = "status",
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
    # Fully-local speech-to-text (:8005) — Parakeet via onnx-asr, OpenAI /v1/audio/transcriptions.
    # CPU EP by default (~no VRAM), so group=llm to coexist with the coder in agent mode.
    "stt"        = @{ script = (Join-Path $serving "start-stt.ps1");     health = "http://127.0.0.1:8005/health";   group = "llm";   desc = "speech-to-text :8005"; port = 8005; ui = $null; vramGB = 1; requires = (Join-Path $root "stt\.venv\Scripts\python.exe") }
    "media"      = @{ script = (Join-Path $serving "start-media.ps1");   health = "http://127.0.0.1:7801/";         group = "media"; desc = "image+video   :7801"; port = 7801; ui = "http://127.0.0.1:7801/"; vramGB = 18 }
    # Tiny always-on prompt-rewriter (:8013) — auto-expands lazy prompts for SwarmUI.
    # group=media so it coexists with the image/video model (and is stopped for the big LLM).
    # 'requires' lets doki skip it cleanly on lean installs where its model isn't present.
    "prompt-rewriter" = @{ script = (Join-Path $serving "start-prompt-rewriter.ps1"); health = "http://127.0.0.1:8013/health"; group = "media"; desc = "prompt rewriter :8013"; port = 8013; ui = $null; vramGB = 3; requires = (Join-Path $root "models\Qwen2.5-3B-Instruct-Q5_K_M.gguf") }
}
$Profiles = [ordered]@{ agent = @("llama-swap", "tts", "stt"); coexist = @("llama-swap", "fim"); media = @("media", "prompt-rewriter") }

function PidFile($n) { Join-Path $runDir "$n.pid" }
function LogFile($n) { Join-Path $runDir "$n.log" }
function IsRunning($n) {
    $pf = PidFile $n
    if (-not (Test-Path $pf)) { return $false }
    # -as [int] coerces non-numeric / partial / multi-line content to $null instead of throwing:
    # Get-Process -Id 'garbage' is a binding error that fires BEFORE -EA can suppress it, which under
    # $ErrorActionPreference='Stop' would abort the whole status/up/start invocation.
    $procId = (Get-Content $pf -ErrorAction SilentlyContinue) -as [int]
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
        $procId = (Get-Content $pf -ErrorAction SilentlyContinue) -as [int]
        if ($procId) { taskkill /PID $procId /T /F *> $null }
        Remove-Item $pf -Force -ErrorAction SilentlyContinue
        Write-Host "  - $n stopped"
    }
    elseif (Probe $Services[$n].health) {
        # up but untracked (started outside doki, no pidfile): kill whatever listens on its port, so a
        # GPU-group switch can actually evict it — otherwise an untracked opposite-group server OOMs 32GB.
        $port = $Services[$n].port
        $owners = if ($port) { @((Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).OwningProcess | Select-Object -Unique) } else { @() }
        if ($owners) { foreach ($op in $owners) { if ($op) { taskkill /PID $op /T /F *> $null } }; Write-Host "  - $n stopped (untracked, by port :$port)" }
        else { Write-Host "  !! $n up untracked on :$port — stop it manually to free the GPU" }
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
    Write-Host "DokiDex services"
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
            # nvidia-smi can return [N/A] for any metric; coerce per-field so one [N/A] degrades ONE
            # field instead of throwing and nuking the whole gauge (was guarded for fan only).
            $num = { param($v) if ("$v" -match '^[\d.]+$') { [double]$v } else { $null } }
            $fan = if ($p.Count -gt 5) { & $num $p[5] } else { $null }
            return [pscustomobject]@{
                usedMB = [int](& $num $p[0]); totalMB = [int](& $num $p[1])
                util = [int](& $num $p[2]); temp = [int](& $num $p[3])
                watts = (& $num $p[4]); fan = $(if ($null -ne $fan) { [int]$fan } else { $null })
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
        if (Test-Path $pf) { $procId = (Get-Content $pf -ErrorAction SilentlyContinue) -as [int] }   # no throw on corrupt pidfile
        $healthy = (Probe $s.health)   # probe ONCE (was probed twice per service)
        $model = $null; $modelState = $null; $configured = @()
        if ($n -eq 'llama-swap' -and $healthy) { $li = LlamaSwapModel; $model = $li.model; $modelState = $li.modelState; $configured = $li.configuredModels }
        [pscustomobject]@{
            name      = $n
            group     = $s.group
            desc      = $s.desc
            port      = $port
            ui        = $s.ui
            vramGB    = $s.vramGB
            health    = $s.health
            healthy   = $healthy
            running   = (IsRunning $n)
            pid       = $procId
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
    foreach ($n in $Services.Keys) { if ($Services[$n].group -ne $wantGroup -and ((IsRunning $n) -or (Probe $Services[$n].health))) { StopSvc $n } }
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
    foreach ($n in $Services.Keys) { if ($Services[$n].group -ne $g -and ((IsRunning $n) -or (Probe $Services[$n].health))) { StopSvc $n } }
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
function Doctor {
    function DL($label, $state, $detail) {
        $color = switch ($state) { "ok" { "Green" } "warn" { "Yellow" } "miss" { "Red" } default { "Gray" } }
        $mark = switch ($state) { "ok" { "  ok " } "warn" { "  !! " } "miss" { " MISS" } default { "  -- " } }
        Write-Host ("{0}  {1,-20} {2}" -f $mark, $label, $detail) -ForegroundColor $color
    }
    Write-Host ""
    Write-Host "DokiDex doctor — environment + install diagnostics"
    Write-Host "===================================================="

    Write-Host "`nHardware"
    try {
        $g = ((& nvidia-smi --query-gpu=name,driver_version,memory.total,memory.used,temperature.gpu '--format=csv,noheader,nounits' 2>$null) | Select-Object -First 1) -split ','
        DL "GPU" "ok" ("{0} · driver {1} · {2}MB ({3}MB used) · {4}C" -f $g[0].Trim(), $g[1].Trim(), [int]([double]$g[2]), [int]([double]$g[3]), $g[4].Trim())
    } catch { DL "GPU" "miss" "nvidia-smi not found — an NVIDIA GPU + driver is required" }
    try { $free = [math]::Round((Get-PSDrive $root.Substring(0, 1)).Free / 1GB); DL "Disk ($($root.Substring(0,2)))" $(if ($free -lt 20) { "warn" } else { "ok" }) "$free GB free" } catch {}

    Write-Host "`nToolchain"
    foreach ($t in @(
            @{n = "pwsh"; opt = $false; d = "control plane shell" }, @{n = "dotnet"; opt = $false; d = "control panel build/test" },
            @{n = "python"; opt = $true; d = "TTS/STT/memory" }, @{n = "uv"; opt = $true; d = "MCP servers" },
            @{n = "git"; opt = $false; d = "" }, @{n = "gh"; opt = $true; d = "update checks" },
            @{n = "crush"; opt = $true; d = "coder CLI" }, @{n = "ffprobe"; opt = $true; d = "verify audio checks" })) {
        $cmd = Get-Command $t.n -ErrorAction SilentlyContinue
        if ($cmd) { DL $t.n "ok" $t.d } else { DL $t.n $(if ($t.opt) { "warn" } else { "miss" }) $(if ($t.opt) { "optional — $($t.d)" } else { "REQUIRED — $($t.d)" }) }
    }
    # `dotnet` present != the right SDK: the panel targets net9.0-windows, so assert the .NET 9 SDK
    # specifically (the existence row above can pass with only .NET 8, and the panel build then fails).
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $sdks = @(dotnet --list-sdks 2>$null | ForEach-Object { ($_ -split '\s+')[0] })
        if ($sdks -match '^9\.0\.') { DL "dotnet 9 SDK" "ok" "net9.0-windows control panel" }
        else { DL "dotnet 9 SDK" "miss" "REQUIRED — panel needs .NET 9 (have: $($sdks -join ', ')); run setup.ps1" }
    }

    Write-Host "`nModels (models\)"
    foreach ($m in @(
            @{n = "coder-fast (Qwen3-30B)"; f = "Qwen3-Coder-30B-A3B-Instruct-UD-Q4_K_XL.gguf" },
            @{n = "coder-big (gpt-oss-120b)"; f = "gpt-oss-120b-mxfp4-*.gguf" },  # multi-part split
            @{n = "FIM (qwen2.5-3b)"; f = "qwen2.5-coder-3b-q8_0.gguf" },
            @{n = "rewriter (qwen2.5-3b)"; f = "Qwen2.5-3B-Instruct-Q5_K_M.gguf" })) {
        $files = @(Get-ChildItem (Join-Path $root "models\$($m.f)") -ErrorAction SilentlyContinue)
        if ($files) { DL $m.n "ok" ("{0} GB{1}" -f [math]::Round(($files | Measure-Object Length -Sum).Sum / 1GB, 1), $(if ($files.Count -gt 1) { " ($($files.Count) parts)" } else { "" })) }
        else { DL $m.n "warn" "not installed" }
    }

    Write-Host "`nMedia kit (media\SwarmUI\Models\)"
    $swDiff = Join-Path $root "media\SwarmUI\Models\diffusion_models"
    DL "SwarmUI backend" $(if (Test-Path (Join-Path $root "media\SwarmUI\src\bin\live_release\SwarmUI.exe")) { "ok" } else { "warn" }) $(if (Test-Path (Join-Path $root "media\SwarmUI\src\bin\live_release\SwarmUI.exe")) { "installed" } else { "run setup.ps1 -Media" })
    foreach ($v in @(
            @{n = "lean: Z-Image Turbo"; f = "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors" }, @{n = "lean: Wan 2.1 1.3B"; f = "wan2.1_t2v_1.3B_fp16.safetensors" },
            @{n = "full: Wan 2.2 5B"; f = "wan2.2_ti2v_5B_fp16.safetensors" }, @{n = "full: Qwen-Image-Edit"; f = "qwen_image_edit_2511_fp8mixed.safetensors" },
            @{n = "full: ACE-Step music"; f = "acestep_v1.5_turbo.safetensors" }, @{n = "full: LTXV fast video"; f = "ltxv-2b-0.9.8-distilled.safetensors" })) {
        DL $v.n $(if (Test-Path (Join-Path $swDiff $v.f)) { "ok" } else { "warn" }) $(if (Test-Path (Join-Path $swDiff $v.f)) { "present" } else { "not installed" })
    }

    Write-Host "`nServices (installable + port)"
    foreach ($n in $Services.Keys) {
        $s = $Services[$n]
        $inst = (-not $s.requires) -or (Test-Path $s.requires)
        $up = Probe $s.health
        $det = "$(if ($inst) { 'installed' } else { 'not installed' })" + "$(if ($up) { ' · port UP' } else { '' })"
        DL "$n :$($s.port)" $(if ($inst) { "ok" } else { "warn" }) $det
    }

    Write-Host "`nExtras"
    $memdb = Join-Path $root "serving\memory-mcp\memory.db"
    DL "memory store" $(if (Test-Path $memdb) { "ok" } else { "warn" }) $(if (Test-Path $memdb) { "seeded ($([math]::Round((Get-Item $memdb).Length/1KB)) KB)" } else { "empty — run serving\memory-mcp\seed.py" })
    DL "control panel" $(if (Test-Path (Join-Path $root "control\bin\Release\net9.0-windows\DokiDex.Control.exe")) { "ok" } else { "warn" }) $(if (Test-Path (Join-Path $root "control\bin\Release\net9.0-windows\DokiDex.Control.exe")) { "built" } else { "not built — dotnet build control\DokiDex.Control.csproj -c Release" })
    Write-Host ""
}
function TailLogs($name) {
    # Each service's stdout and stderr are redirected to SEPARATE files at launch (.log / .log.err).
    # Several servers (llama-server for fim/prompt-rewriter, uvicorn for tts/stt) log almost entirely
    # to STDERR, so following only .log shows an empty file — follow BOTH live, newest bytes from each.
    $files = @((LogFile $name), "$(LogFile $name).err") | Where-Object { Test-Path $_ }
    if (-not $files) { Write-Host "no log for '$name' yet (start it with: .\doki.ps1 up)"; return }
    Write-Host "== tailing $($files.Count) stream(s) for '$name'  (Ctrl+C to stop) ==" -ForegroundColor DarkGray
    foreach ($f in $files) { Get-Content $f -Tail 20 -ErrorAction SilentlyContinue }
    $pos = @{}; foreach ($f in $files) { $pos[$f] = (Get-Item $f).Length }
    while ($true) {
        Start-Sleep -Milliseconds 700
        foreach ($f in $files) {
            try {
                $len = (Get-Item $f).Length
                if ($len -lt $pos[$f]) { $pos[$f] = 0 }   # truncated/rotated -> re-read from start
                if ($len -gt $pos[$f]) {
                    $fs = [System.IO.File]::Open($f, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                    try {
                        $fs.Seek($pos[$f], [System.IO.SeekOrigin]::Begin) | Out-Null
                        $chunk = (New-Object System.IO.StreamReader($fs)).ReadToEnd()
                    } finally { $fs.Dispose() }
                    if ($chunk) { Write-Host -NoNewline $chunk }
                    $pos[$f] = $len
                }
            } catch {}
        }
    }
}
function LaunchPanel {
    $exe = Join-Path $root "control\bin\Release\net9.0-windows\DokiDex.Control.exe"
    $proj = Join-Path $root "control\DokiDex.Control.csproj"
    # Build the exe on first run rather than `dotnet run` — the latter keeps a visible console window
    # open for the app's whole life, the exact console-flash the boot sequence + .lnk exist to kill.
    if (-not (Test-Path $exe) -and (Test-Path $proj)) {
        if (Get-Command dotnet -ErrorAction SilentlyContinue) {
            Write-Host "building control panel (first run) ..."
            & dotnet build $proj -c Release
            if ($LASTEXITCODE -ne 0) { Write-Host "panel build failed — run .\control.bat to see the errors"; return }
        } else { Write-Host "dotnet not found — run setup.ps1 (installs the .NET 9 SDK) or .\control.bat"; return }
    }
    if (Test-Path $exe) {
        # ensure the premium console-free launcher exists (DokiDex.lnk -> exe, arc-reactor icon)
        if (-not (Test-Path (Join-Path $root "DokiDex.lnk"))) { try { & pwsh -NoProfile -File (Join-Path $root "control\make-shortcut.ps1") | Out-Null } catch {} }
        Start-Process $exe
    } else { Write-Host "control panel project not found at $proj" }
}

switch ($Command) {
    "up"      { DoUp $Arg }
    "down"    { DoDown }
    "restart" { RestartArg $Arg }
    "start"   { if (-not $Arg) { throw "usage: .\doki.ps1 start <service>" }; StartOne $Arg }
    "stop"    { if (-not $Arg) { throw "usage: .\doki.ps1 stop <service>" }; StopOne $Arg }
    "panel"   { LaunchPanel }
    "doctor"  { Doctor }
    "status"  { if ($Arg -eq "json" -or $Arg -eq "--json") { StatusJson } else { ShowStatus } }
    "logs" {
        if (-not $Arg) { throw "usage: .\doki.ps1 logs <$($Services.Keys -join '|')>" }
        if (-not $Services.Contains($Arg)) { throw "unknown service '$Arg' — one of: $($Services.Keys -join ', ')" }
        TailLogs $Arg
    }
    "verify" { & (Join-Path $root "verify.ps1") }
    "test" {
        # unit tests (no GPU compute; fast). Live capability smokes are `doki verify`.
        $failed = 0
        # 1. PowerShell suites — installer failure-recovery helpers + the `status json` contract
        #    the panel parses. Each runs in a child pwsh so its `exit` can't tear down this run.
        foreach ($rel in @("tests\setup-helpers.test.ps1", "tests\doki-statusjson.test.ps1")) {
            $tp = Join-Path $root $rel
            if (Test-Path $tp) {
                Write-Host "== $(Split-Path $tp -Leaf) ==" -ForegroundColor Cyan
                & pwsh -NoProfile -File $tp
                if ($LASTEXITCODE -ne 0) { $failed = 1 }
            }
        }
        # 2. persistent-memory store (sqlite + FTS5) — pure-stdlib python; skipped if python absent.
        $memTest = Join-Path $root "tests\memory_db.test.py"
        if (Test-Path $memTest) {
            if (Get-Command python -ErrorAction SilentlyContinue) {
                Write-Host "`n== memory_db.test.py ==" -ForegroundColor Cyan
                & python $memTest
                if ($LASTEXITCODE -ne 0) { $failed = 1 }
            } else { Write-Host "`n~ memory_db.test.py skipped (python not found)" }
        }
        # 3. control-panel data layer (xUnit).
        $proj = Join-Path $root "control\DokiDex.Control.Tests\DokiDex.Control.Tests.csproj"
        if (Test-Path $proj) {
            Write-Host "`n== control-panel unit tests ==" -ForegroundColor Cyan
            & dotnet test $proj
            if ($LASTEXITCODE -ne 0) { $failed = 1 }
        } else { Write-Host "panel test project not present" }
        exit $failed
    }
}
