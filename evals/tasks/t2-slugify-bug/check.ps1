param([Parameter(Mandatory)][string]$Work)
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests failed"; exit 1 }
$tc = Get-Content tests/App.Tests/StringUtilsTests.cs -Raw
foreach ($must in @('hello-world', 'foo-bar', 'multiple-spaces', 'c-is-great')) {
    if ($tc -notmatch [regex]::Escape($must)) { Write-Host "CHECK: test assertions were modified"; exit 1 }
}
Write-Host "CHECK: PASS"
exit 0
