# Start the FIM (fill-in-the-middle) autocomplete server for llama.vscode.
# Dedicated llama-server on :8012 exposing the /infill endpoint, independent
# of llama-swap (:8080) so editor completions and the agent can coexist.
#
# VRAM: FIM 3B Q8 ≈ ~4GB. The agent's coder-fast uses ~30GB at 128k ctx, so
# running BOTH simultaneously requires coder-fast at reduced ctx (see docs).
# Standalone (editor only), this leaves the GPU nearly empty.
#
# Usage:  .\start-fim.ps1 [-Detach] [-PidFile <p>] [-LogFile <l>]
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$server = Join-Path $PSScriptRoot "llama.cpp\llama-server.exe"
$model  = Join-Path (Split-Path $PSScriptRoot) "models\qwen2.5-coder-3b-q8_0.gguf"

$argList = @(
    "-m", $model,
    "--port", "8012",
    "--host", "127.0.0.1",
    "-ngl", "99",
    "-c", "16384",
    "-fa", "on",
    "--ubatch-size", "1024",
    "--batch-size", "2048",
    "--cache-reuse", "256"
)

if ($Detach) {
    $sp = @{ FilePath = $server; ArgumentList = $argList; WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "FIM server starting in background on http://127.0.0.1:8012 (/infill, pid $($p.Id))"
} else {
    & $server @argList
}
