param([Parameter(Mandatory)][string]$Work)
Copy-Item (Join-Path $PSScriptRoot "hidden\*.cs") (Join-Path $Work "tests\App.Tests\") -Force
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests (incl. hidden) failed"; exit 1 }
$tc = (Get-Content tests/App.Tests/DurationTests.cs -Raw) -join "`n"
foreach ($must in @('InlineData(0, "0s")', 'InlineData(3661, "1h 1m 1s")', 'InlineData(90061, "1d 1h 1m 1s")')) {
    if (-not $tc.Contains($must)) { Write-Host "CHECK: provided Humanize tests were modified"; exit 1 }
}
Write-Host "CHECK: PASS"
exit 0
