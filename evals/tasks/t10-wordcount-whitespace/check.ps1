param([Parameter(Mandatory)][string]$Work)
Copy-Item (Join-Path $PSScriptRoot "hidden\*.cs") (Join-Path $Work "tests\App.Tests\") -Force
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests (incl. hidden) failed"; exit 1 }
$tc = (Get-Content tests/App.Tests/*.cs -Raw) -join "`n"
if ($tc -notmatch '\\t|\\n|Tab|tab') { Write-Host "CHECK: no visible test added for tab/newline"; exit 1 }
Write-Host "CHECK: PASS"
exit 0
