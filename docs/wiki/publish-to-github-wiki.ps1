# Publish docs/wiki/*.md to the GitHub project wiki (defessler/DokiCode.wiki).
#
# GitHub wikis are a separate git repo with their own link rules, so this script
# transforms the in-repo Markdown before pushing:
#   - links between wiki pages have ".md" stripped   (page.md -> page)
#   - ../<doc>.md links become absolute github.com blob URLs (they live in the
#     main repo, not the wiki)
# It also generates a _Sidebar.md nav. Clones the wiki if it exists, otherwise
# initializes it (bootstrapping a brand-new wiki on first push).
#
# Usage:  .\docs\wiki\publish-to-github-wiki.ps1
param(
    [string]$WikiRemote = "git@github.com:defessler/DokiCode.wiki.git",
    [string]$BlobBase   = "https://github.com/defessler/DokiCode/blob/main/docs"
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false   # we branch on git exit codes

$src   = $PSScriptRoot
$build = Join-Path $env:TEMP ("doki-wiki-" + [guid]::NewGuid().ToString("n").Substring(0, 8))

# --- Get a working copy of the wiki: clone if it exists, else init fresh -------
git clone $WikiRemote $build 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Wiki repo not found remotely - initializing a new one."
    New-Item -ItemType Directory -Force $build | Out-Null
    git -C $build -c init.defaultBranch=master init | Out-Null
    git -C $build remote add origin $WikiRemote
} else {
    Write-Host "Cloned existing wiki - re-syncing."
    Get-ChildItem $build -Filter *.md -File | Remove-Item -Force
}

# --- Copy + transform pages ---------------------------------------------------
$pages = Get-ChildItem $src -Filter *.md -File
$slugs = $pages | ForEach-Object { $_.BaseName }
foreach ($p in $pages) {
    $text = Get-Content $p.FullName -Raw
    foreach ($s in $slugs) {
        $text = $text.Replace("$s.md#", "$s#").Replace("$s.md)", "$s)")
    }
    $text = [regex]::Replace($text, '\.\./([A-Za-z0-9\-]+)\.md', "$BlobBase/`$1.md")
    Set-Content (Join-Path $build $p.Name) $text -NoNewline
}

# --- Sidebar nav --------------------------------------------------------------
@'
### DokiCode Wiki

**Read in order**

1. [The Big Idea](1-the-big-idea)
2. [The Moving Parts](2-the-moving-parts)
3. [Watch It Solve a Task](3-a-task-step-by-step)
4. [Local vs. the Big Clouds](4-local-vs-the-big-clouds)
5. [Why It's Built This Way](5-why-its-built-this-way)
6. [Glossary](6-glossary)
7. [Quick Start](7-quick-start)

[🏠 Home](Home)
'@ | Set-Content (Join-Path $build "_Sidebar.md")

# --- Commit + push ------------------------------------------------------------
git -C $build add -A
git -C $build commit -m "Sync wiki from docs/wiki" | Out-Null
git -C $build push -u origin HEAD:master
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "PUSH FAILED. If this is the first time, the wiki may need one page"
    Write-Host "created via the web UI first: repo -> Wiki -> Create the first page"
    Write-Host "-> Save, then re-run this script."
    exit 1
}
Write-Host ""
Write-Host "Wiki published -> https://github.com/defessler/DokiCode/wiki"
