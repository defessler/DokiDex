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

# Hide the GPU from the EMBED CHILD: a CUDA-built llama-server still grabs a ~100-300MB CUDA context at
# -ngl 0, which would quietly steal VRAM the coder needs. With no visible device it runs pure CPU -> a
# truly 0-VRAM embed server that coexists with the full-size coder. CUDA_VISIBLE_DEVICES=-1 is scoped to
# the launch below (set -> launch [child snapshots the env] -> restore), so it never leaks into the parent
# doki session and blinds later GPU services like TTS/STT. (P1 audit fix.)

$server = Join-Path $PSScriptRoot "llama.cpp\llama-server.exe"
$model  = Join-Path (Split-Path $PSScriptRoot) "models\nomic-embed-text-v1.5.f16.gguf"
if (-not (Test-Path $model)) { throw "embed model not found at $model — run .\setup.ps1 (fetches nomic-embed-text-v1.5)" }

$argList = @(
    "-m", $model,
    "--port", "8090",
    "--host", "127.0.0.1",
    "--embedding",            # expose POST /v1/embeddings
    "--pooling", "mean",      # nomic-embed-text uses mean pooling
    "-ngl", "0",              # CPU-only: zero VRAM, never competes for the GPU
    "-c", "2048",             # nomic-embed's native max sequence length; chunks are capped to fit
    "-b", "2048", "-ub", "2048",  # batch+ubatch must cover a whole chunk — the default ubatch 512 silently drops longer inputs
    "--alias", "embed"        # id in /v1/models + /v1/embeddings (code_index.py EMBED_MODEL default)
)

# Scope CUDA_VISIBLE_DEVICES=-1 to the embed child ONLY: set it, launch (Start-Process / `&` snapshot the
# parent env into the child), then restore the parent in `finally` so later doki children (TTS/STT) keep
# their GPU. `finally` also runs on the early `return` below. (P1 audit fix: was leaking -1 into the session.)
$prevCuda = $env:CUDA_VISIBLE_DEVICES
$env:CUDA_VISIBLE_DEVICES = "-1"
try {
    if ($Detach) {
        $sp = @{ FilePath = $server; ArgumentList = $argList; WindowStyle = "Hidden"; PassThru = $true }
        if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
        $p = Start-Process @sp
        Start-Sleep -Milliseconds 400   # a doomed launch (bad args / corrupt model / missing dll) exits within this window
        if ($p.HasExited) { Write-Warning "embed server exited immediately (code $($p.ExitCode))$(if ($LogFile) { "; see $LogFile.err" }) — not writing pid"; return }
        if ($PidFile) { Set-Content $PidFile $p.Id }
        Write-Host "embed server starting in background on http://127.0.0.1:8090 (pid $($p.Id))"
    } else {
        & $server @argList
    }
} finally {
    if ($null -eq $prevCuda) { Remove-Item Env:\CUDA_VISIBLE_DEVICES -ErrorAction SilentlyContinue }
    else { $env:CUDA_VISIBLE_DEVICES = $prevCuda }
}
