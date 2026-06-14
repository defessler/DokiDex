# Start the prompt-rewriter — a tiny always-on LLM that auto-expands short prompts
# into the rich, cinematic prompts the image/video models were trained on.
# Dedicated llama-server on :8013, separate from llama-swap (:8080), so it can
# COEXIST with the image/video model during media generation (SwarmUI MagicPrompt
# calls it via http://127.0.0.1:8013). This is what makes "great output from lazy
# prompts" work — see docs/wiki/8-image-and-video.md.
#
# VRAM: Qwen2.5-3B Q5 ≈ ~2.5GB. The active Wan video expert (~14GB fp8) + text
# encoder leave ample room, so the rewriter and the video model share the 32GB
# card (peak ≈ ~24GB). It runs in doki's "media" group, NOT alongside the big
# coder LLM (which is GPU-exclusive with media).
#
# Usage:  .\start-prompt-rewriter.ps1 [-Detach] [-PidFile <p>] [-LogFile <l>]
param([switch]$Detach, [string]$PidFile, [string]$LogFile)

$server = Join-Path $PSScriptRoot "llama.cpp\llama-server.exe"
$model  = Join-Path (Split-Path $PSScriptRoot) "models\Qwen2.5-3B-Instruct-Q5_K_M.gguf"

$argList = @(
    "-m", $model,
    "--port", "8013",
    "--host", "127.0.0.1",
    "-ngl", "99",
    "-c", "8192",
    "-fa", "on",
    "--jinja",                 # instruct chat template (MagicPrompt sends /v1/chat/completions)
    "--alias", "prompt-rewriter"  # clean id in /v1/models for MagicPrompt's model field
)

if ($Detach) {
    $sp = @{ FilePath = $server; ArgumentList = $argList; WindowStyle = "Hidden"; PassThru = $true }
    if ($LogFile) { $sp.RedirectStandardOutput = $LogFile; $sp.RedirectStandardError = "$LogFile.err" }
    $p = Start-Process @sp
    if ($PidFile) { Set-Content $PidFile $p.Id }
    Write-Host "prompt-rewriter starting in background on http://127.0.0.1:8013 (pid $($p.Id))"
} else {
    & $server @argList
}
