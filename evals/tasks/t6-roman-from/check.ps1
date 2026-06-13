param([Parameter(Mandatory)][string]$Work)
Copy-Item (Join-Path $PSScriptRoot "hidden\*.cs") (Join-Path $Work "tests\App.Tests\") -Force
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests (incl. hidden) failed"; exit 1 }
$tc = (Get-Content tests/App.Tests/RomanFromTests.cs -Raw) -join "`n"
foreach ($must in @('"MCMXCIV", 1994', '"MMMCMXCIX", 3999', '[InlineData("Q")]')) {
    if (-not $tc.Contains($must)) { Write-Host "CHECK: provided FromRoman tests were modified"; exit 1 }
}
Write-Host "CHECK: PASS"
exit 0
