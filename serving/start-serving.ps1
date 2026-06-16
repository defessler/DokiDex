# Start the DokiCode local inference endpoint (llama-swap on :8080).
# Usage:  .\start-serving.ps1                                   (foreground, logs to console)
#         .\start-serving.ps1 -Detach                           (background process)
#         .\start-serving.ps1 -Detach -PidFile <p> -LogFile <l> (used by ..\doki.ps1)
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$swap   = Join-Path $PSScriptRoot "llama-swap\llama-swap.exe"
$listen = "127.0.0.1:8080"

# llama-swap.yaml is a TEMPLATE: __DOKI_ROOT__ stands in for the repo root so a project move can't
# break the absolute paths llama-server needs. llama-swap doesn't expand ${ENV} inside `cmd`, so we
# substitute here and emit a concrete config into .run\ (regenerated every launch from the current
# location), then point llama-swap at that.
$root   = Split-Path $PSScriptRoot
$runDir = Join-Path $root ".run"; New-Item -ItemType Directory -Force $runDir | Out-Null
$config = Join-Path $runDir "llama-swap.generated.yaml"
(Get-Content (Join-Path $PSScriptRoot "llama-swap.yaml") -Raw).Replace('__DOKI_ROOT__', $root) | Set-Content $config

if ($Detach) {
    $sp = @{ FilePath = $swap; ArgumentList = @("--config", $config, "--listen", $listen); WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "llama-swap started in background on http://$listen (pid $($p.Id), UI: http://$listen/ui)"
} else {
    & $swap --config $config --listen $listen
}
