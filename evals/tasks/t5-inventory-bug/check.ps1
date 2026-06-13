param([Parameter(Mandatory)][string]$Work)
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests failed"; exit 1 }
$tc = (Get-Content tests/App.Tests/ConverterAndParserTests.cs -Raw) -join "`n"
foreach ($must in @('inv.Add("apple", 3)', 'inv.Add("Apple", 2)', 'Assert.Equal(5, inv.CountOf("apple"))')) {
    if (-not $tc.Contains($must)) { Write-Host "CHECK: inventory test assertions were modified"; exit 1 }
}
Write-Host "CHECK: PASS"
exit 0
