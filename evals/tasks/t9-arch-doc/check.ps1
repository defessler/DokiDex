param([Parameter(Mandatory)][string]$Work)
Set-Location $Work
$doc = "docs/ARCHITECTURE.md"
if (-not (Test-Path $doc)) { Write-Host "CHECK: docs/ARCHITECTURE.md not created"; exit 1 }
$content = Get-Content $doc -Raw
foreach ($name in @("StringUtils", "CsvParser", "Inventory", "TemperatureConverter", "RomanNumeral", "Program")) {
    if ($content -notmatch $name) { Write-Host "CHECK: missing coverage of $name"; exit 1 }
}
if (($content -split '\s+').Count -lt 150) { Write-Host "CHECK: under 150 words"; exit 1 }
if (([regex]::Matches($content, '(?m)^#{1,3} ')).Count -lt 2) { Write-Host "CHECK: needs at least 2 markdown headings"; exit 1 }
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests broken by doc task"; exit 1 }
Write-Host "CHECK: PASS"
exit 0
