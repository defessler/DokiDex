param([Parameter(Mandatory)][string]$Work)
Set-Location $Work
dotnet build --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: build failed"; exit 1 }
dotnet test --nologo -v q *> $null
if ($LASTEXITCODE) { Write-Host "CHECK: tests failed"; exit 1 }
$out = @(dotnet run --project src/App -- --stats hello world 2>$null)
if ($out.Count -lt 2 -or $out[0].Trim() -ne "words=2" -or $out[1].Trim() -ne "chars=11") {
    Write-Host "CHECK: --stats output wrong: '$($out -join ' | ')' (expected words=2 / chars=11)"; exit 1
}
$slug = (dotnet run --project src/App -- --slug Hello World 2>$null) -join "`n"
if ($slug.Trim() -ne "hello-world") { Write-Host "CHECK: --slug regressed"; exit 1 }
Write-Host "CHECK: PASS"
exit 0
