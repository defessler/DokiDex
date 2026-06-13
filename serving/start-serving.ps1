# Start the DokiCode local inference endpoint (llama-swap on :8080).
# Usage:  .\start-serving.ps1                                   (foreground, logs to console)
#         .\start-serving.ps1 -Detach                           (background process)
#         .\start-serving.ps1 -Detach -PidFile <p> -LogFile <l> (used by ..\doki.ps1)
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$swap   = Join-Path $PSScriptRoot "llama-swap\llama-swap.exe"
$config = Join-Path $PSScriptRoot "llama-swap.yaml"
$listen = "127.0.0.1:8080"

if ($Detach) {
    $sp = @{ FilePath = $swap; ArgumentList = @("--config", $config, "--listen", $listen); WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "llama-swap started in background on http://$listen (pid $($p.Id), UI: http://$listen/ui)"
} else {
    & $swap --config $config --listen $listen
}
