# Start the code-embedding server — a tiny CPU-only llama-server that turns text into vectors for the
# codebase RAG (the `code_search` MCP tool + serving/memory-mcp/code_index.py). Dedicated server on :8090,
# OpenAI POST /v1/embeddings, SEPARATE from llama-swap (:8080).
#
# CPU-only (-ngl 0) BY DESIGN: nomic-embed-text-v1.5 is ~140MB and embeds a query in tens of ms on CPU, so
# it claims ZERO VRAM and can stay up during coding (or gaming) without ever contending for the 32GB card.
# A full-repo index is a slower one-time batch; raise -ngl if you want it on the GPU.
#
# Usage:  .\start-embed.ps1 [-Detach] [-PidFile <p>] [-LogFile <l>]
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$server = Join-Path $PSScriptRoot "llama.cpp\llama-server.exe"
$model  = Join-Path (Split-Path $PSScriptRoot) "models\nomic-embed-text-v1.5.f16.gguf"

$argList = @(
    "-m", $model,
    "--port", "8090",
    "--host", "127.0.0.1",
    "--embedding",            # expose POST /v1/embeddings
    "--pooling", "mean",      # nomic-embed-text uses mean pooling
    "-ngl", "0",              # CPU-only: zero VRAM, never competes for the GPU
    "-c", "8192",             # enough context for a ~60-line code chunk
    "--alias", "embed"        # id in /v1/models + /v1/embeddings (code_index.py EMBED_MODEL default)
)

if ($Detach) {
    $sp = @{ FilePath = $server; ArgumentList = $argList; WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "embed server starting in background on http://127.0.0.1:8090 (pid $($p.Id))"
} else {
    & $server @argList
}
