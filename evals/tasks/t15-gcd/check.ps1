param([Parameter(Mandatory)][string]$Work)
Copy-Item (Join-Path $PSScriptRoot "hidden\*.cs") (Join-Path $Work "tests\App.Tests\") -Force
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests (incl. hidden) failed"; exit 1 }
$tc = (Get-Content tests/App.Tests/MathUtilTests.cs -Raw) -join "`n"
foreach ($must in @('InlineData(12, 8, 4)', 'InlineData(0, 5, 5)', 'InlineData(100, 10, 10)')) {
    if (-not $tc.Contains($must)) { Write-Host "CHECK: provided Gcd tests were modified"; exit 1 }
}
Write-Host "CHECK: PASS"
exit 0
