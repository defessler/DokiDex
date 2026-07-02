# Builds the embedded runtime payload (control\obj\…\payload.zip) that the installed app extracts to its home,
# so the app is self-contained — no cloned repo. Runtime SCRIPTS/CONFIGS only; NOT the heavy downloaded assets
# (models/, media/, tts/, stt/) or binaries. Invoked by the csproj StagePayload target.  Run manually:
#   pwsh -File control\make-payload.ps1 -Root . -Out control\obj\payload.zip
param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$root = (Resolve-Path $Root).Path
$Out  = [System.IO.Path]::GetFullPath($Out)
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("dokidex-payload-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $staging | Out-Null
try {
    # top-level runtime scripts
    foreach ($f in 'doki.ps1', 'verify.ps1', 'setup.ps1') {
        $src = Join-Path $root $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $staging $f) -Force }
    }
    # Help view (3.2) docs -- the EXACT whitelist DocsCatalog.cs serves (README + the 4 core guides + docs/wiki/
    # *.md), never the whole docs/ tree (which also holds internal audit/research notes not meant for end users).
    # Without this, an installed app's home (RepoPaths.Root) never gets a docs/ folder and the Help view is empty.
    $readme = Join-Path $root 'README.md'
    if (Test-Path $readme) { Copy-Item $readme (Join-Path $staging 'README.md') -Force }
    foreach ($f in 'quickstart.md', 'tutorial.md', 'CAPABILITIES.md') {
        $src = Join-Path $root "docs\$f"
        if (Test-Path $src) {
            New-Item -ItemType Directory -Force (Join-Path $staging 'docs') | Out-Null
            Copy-Item $src (Join-Path $staging "docs\$f") -Force
        }
    }
    $wikiSrc = Join-Path $root 'docs\wiki'
    if (Test-Path $wikiSrc) {
        $wikiDest = Join-Path $staging 'docs\wiki'
        New-Item -ItemType Directory -Force $wikiDest | Out-Null
        Get-ChildItem $wikiSrc -Filter '*.md' -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $wikiDest $_.Name) -Force }
    }
    # runtime directories, minus binaries / weights / caches / DBs (heavy or machine-specific)
    $excludeExt = @('.exe', '.dll', '.gguf', '.safetensors', '.bin', '.pyc', '.db', '.log')
    foreach ($d in 'serving', 'harness', 'media-assets') {
        $srcDir = Join-Path $root $d
        if (-not (Test-Path $srcDir)) { continue }
        Get-ChildItem $srcDir -Recurse -File | Where-Object {
            $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/')
            ($rel -notmatch '[\\/]llama\.cpp[\\/]') -and
            ($rel -notmatch '[\\/]__pycache__[\\/]') -and
            ($excludeExt -notcontains $_.Extension.ToLower())
        } | ForEach-Object {
            $rel  = $_.FullName.Substring($root.Length).TrimStart('\', '/')
            $dest = Join-Path $staging $rel
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item $_.FullName $dest -Force
        }
    }
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    if (Test-Path $Out) { Remove-Item $Out -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $Out)
    Write-Host "payload -> $Out ($([math]::Round((Get-Item $Out).Length / 1KB)) KB)"
}
finally { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
