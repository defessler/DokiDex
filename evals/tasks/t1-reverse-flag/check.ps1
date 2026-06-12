param([Parameter(Mandatory)][string]$Work)
Set-Location $Work
dotnet build --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: build failed"; exit 1 }
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests failed"; exit 1 }
$out = (dotnet run --project src/App -- --reverse hello 2>$null) -join "`n"
if ($out.Trim() -ne "olleh") { Write-Host "CHECK: --reverse output wrong: '$($out.Trim())'"; exit 1 }
$testsContent = (Get-Content tests/App.Tests/*.cs -Raw) -join "`n"   # -Raw on a glob returns an array; join before matching
if ($testsContent -notmatch "(?i)reverse") { Write-Host "CHECK: no test added for reverse"; exit 1 }
Write-Host "CHECK: PASS"
exit 0
