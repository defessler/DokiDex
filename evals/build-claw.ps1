# Build the 'claw' binary for the eval bake-off (Claw Code, local-provider fork).
# Clones codetwentyfive/claw-code-local if needed, then cargo-builds the CLI.
# Requires the Rust toolchain (rustup). Prints the resulting claw.exe path; the
# eval runner (run-eval.ps1 -Harness claw) then picks it up automatically.
#
# Usage:  .\evals\build-claw.ps1
#         .\evals\build-claw.ps1 -Src D:\some\other\checkout
param([string]$Src = "$env:LOCALAPPDATA\claw-code-local")
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false   # branch on git/cargo exit codes

# 1. Source checkout
if (-not (Test-Path (Join-Path $Src "rust"))) {
    Write-Host "Cloning claw-code-local -> $Src"
    git clone --depth 1 https://github.com/codetwentyfive/claw-code-local.git $Src
    if ($LASTEXITCODE -ne 0) { throw "git clone failed ($LASTEXITCODE)" }
} else {
    Write-Host "Using existing checkout: $Src"
}

# 2. Rust toolchain
$cargoCmd = Get-Command cargo -ErrorAction SilentlyContinue
$cargo = if ($cargoCmd) {
    $cargoCmd.Source
} elseif (Test-Path "$env:USERPROFILE\.cargo\bin\cargo.exe") {
    "$env:USERPROFILE\.cargo\bin\cargo.exe"
} else {
    $null
}
if (-not $cargo) {
    Write-Host ""
    Write-Host "Rust toolchain not found. Install it, then re-run this script:"
    Write-Host "  winget install Rustlang.Rustup"
    Write-Host "  rustup default stable-x86_64-pc-windows-gnu   # -gnu avoids needing VS C++ build tools"
    exit 1
}

# 3. Build (first run pulls a few hundred crates - give it a few minutes)
Write-Host "Building rusty-claude-cli (release) with $cargo ..."
& $cargo build --manifest-path (Join-Path $Src "rust\Cargo.toml") -p rusty-claude-cli --release
if ($LASTEXITCODE -ne 0) { throw "cargo build failed ($LASTEXITCODE)" }

$exe = Join-Path $Src "rust\target\release\claw.exe"
if (-not (Test-Path $exe)) { throw "build succeeded but $exe is missing" }
Write-Host ""
Write-Host "Built: $exe"
Write-Host "Run the bake-off:  .\evals\run-suite.ps1 -Harness claw -Model coder-fast"
