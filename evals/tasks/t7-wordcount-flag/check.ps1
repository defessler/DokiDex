param([Parameter(Mandatory)][string]$Work)
Set-Location $Work
dotnet build --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: build failed"; exit 1 }
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests failed"; exit 1 }
$wc = (dotnet run --project src/App -- --wordcount one two three 2>$null) -join "`n"
if ($wc.Trim() -ne "3") { Write-Host "CHECK: --wordcount wrong: '$($wc.Trim())'"; exit 1 }
$slug = (dotnet run --project src/App -- --slug Hello World 2>$null) -join "`n"
if ($slug.Trim() -ne "hello-world") { Write-Host "CHECK: --slug regressed: '$($slug.Trim())'"; exit 1 }
$echo = (dotnet run --project src/App -- plain text 2>$null) -join "`n"
if ($echo.Trim() -ne "plain text") { Write-Host "CHECK: echo regressed: '$($echo.Trim())'"; exit 1 }
Write-Host "CHECK: PASS"
exit 0
