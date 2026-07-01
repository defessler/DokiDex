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
# FIM model: Qwen2.5-Coder-3B. llama.vscode + llama-server's /infill handle Qwen's FIM tokens
# (<|fim_prefix|>{prefix}<|fim_suffix|>{suffix}<|fim_middle|>) natively — nothing to build here; this is the
# canonical llama.vscode setup. NB (docs/mistral-2026-06.md): swapping in Codestral would need a SUFFIX-FIRST raw
# prompt (<s>[SUFFIX]{suffix}[PREFIX]{prefix}, and Codestral has NO [MIDDLE] token) — /infill's default order and
# any appended [MIDDLE] would be WRONG for it. Keep Qwen here unless that prompt construction is added.
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
