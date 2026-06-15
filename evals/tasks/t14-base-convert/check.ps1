param([Parameter(Mandatory)][string]$Work)
Copy-Item (Join-Path $PSScriptRoot "hidden\*.cs") (Join-Path $Work "tests\App.Tests\") -Force
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests (incl. hidden) failed"; exit 1 }
$tc = (Get-Content tests/App.Tests/BaseConverterTests.cs -Raw) -join "`n"
foreach ($must in @('InlineData(255, 16, "ff")', 'InlineData(0, 2, "0")', 'InlineData(10, 2, "1010")')) {
    if (-not $tc.Contains($must)) { Write-Host "CHECK: provided ToBase tests were modified"; exit 1 }
}
Write-Host "CHECK: PASS"
exit 0
