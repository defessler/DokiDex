# Start the DokiCode local inference endpoint (llama-swap on :8080).
# Usage:  .\start-serving.ps1            (foreground, logs to console)
#         .\start-serving.ps1 -Detach    (background process)
param([switch]$Detach)

$swap   = Join-Path $PSScriptRoot "llama-swap\llama-swap.exe"
$config = Join-Path $PSScriptRoot "llama-swap.yaml"
$listen = "127.0.0.1:8080"

if ($Detach) {
    Start-Process -FilePath $swap -ArgumentList "--config", $config, "--listen", $listen -WindowStyle Hidden
    Write-Host "llama-swap started in background on http://$listen (UI: http://$listen/ui)"
} else {
    & $swap --config $config --listen $listen
}
