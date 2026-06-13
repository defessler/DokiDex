param([Parameter(Mandatory)][string]$Work)
Copy-Item (Join-Path $PSScriptRoot "hidden\*.cs") (Join-Path $Work "tests\App.Tests\") -Force
Set-Location $Work
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests (incl. hidden) failed — behavior changed"; exit 1 }
$src = Get-Content src/App/ReportFormatter.cs -Raw
foreach ($must in @("FormatHeader", "FormatFooter")) {
    if ($src -notmatch $must) { Write-Host "CHECK: public method $must missing"; exit 1 }
}
# Duplication gone: the loop body appeared twice before; after extraction it must appear at most once.
$marker = [regex]::Matches($src, [regex]::Escape("if (!lastWasSpace) sb.Append(' ');")).Count
if ($marker -gt 1) { Write-Host "CHECK: normalization logic still duplicated ($marker copies)"; exit 1 }
Write-Host "CHECK: PASS"
exit 0
