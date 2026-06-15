param([Parameter(Mandatory)][string]$Work)
Copy-Item (Join-Path $PSScriptRoot "hidden\*.cs") (Join-Path $Work "tests\App.Tests\") -Force
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests (incl. hidden) failed"; exit 1 }
$tc = (Get-Content tests/App.Tests/IntervalTests.cs -Raw) -join "`n"
foreach ($must in @('InlineData("1-4,4-5", "1-5")', 'InlineData("1-3,2-6,8-10,15-18", "1-6,8-10,15-18")', 'InlineData("", "")')) {
    if (-not $tc.Contains($must)) { Write-Host "CHECK: provided Merge tests were modified"; exit 1 }
}
Write-Host "CHECK: PASS"
exit 0
