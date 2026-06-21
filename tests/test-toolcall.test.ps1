# tests/test-toolcall.test.ps1 — unit tests for serving/test-toolcall.ps1's pure tool-call helpers.
#
# Hardens the model bake-off GATE. The acceptance script gained a forced-tool-call test
# (tool_choice=required) and a multi-hop-loop-termination test to catch the GLM-4.7-Flash failure
# mode (llama.cpp #19009 — ignores tool_choice / loops forever in required+thinking mode) that a
# single auto-mode call at temp 0.2 cannot surface. These pin the pure request-building /
# response-parsing / transcript-shaping logic by AST (no live model, no side effects).
# Framework-free: exit 0 = all pass, 1 = fail. Run via `doki test`.

$ErrorActionPreference = "Stop"
$tc = Join-Path $PSScriptRoot "..\serving\test-toolcall.ps1"
if (-not (Test-Path $tc)) { Write-Error "test-toolcall.ps1 not found at $tc"; exit 2 }

# import the pure helpers by name (parse only; the script's live-POST body never executes)
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $tc).Path, [ref]$null, [ref]$null)
$fnText = @('New-ToolReq', 'Read-ToolResp', 'Add-ToolResultTurns') | ForEach-Object {
    $name = $_
    $fn = $ast.FindAll({ param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $x.Name -eq $name }, $true) | Select-Object -First 1
    if (-not $fn) { throw "function '$name' not found in test-toolcall.ps1 (renamed? the test must track it)" }
    $fn.Extent.Text
}
. ([scriptblock]::Create($fnText -join "`n`n"))

$script:pass = 0; $script:fail = 0
function Assert($cond, $msg) {
    if ($cond) { $script:pass++; Write-Host "  [PASS] $msg" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
}

$tool = @{ type = "function"; function = @{ name = "get_weather"; description = "x"; parameters = @{ type = "object"; properties = @{ city = @{ type = "string" } }; required = @("city") } } }
$umsgs = @(@{ role = "user"; content = "weather in Tokyo?" })

Write-Host "`nNew-ToolReq (sampling + tool_choice wiring)"
$def = New-ToolReq -Model "coder-fast" -Messages $umsgs -Tools @($tool) -Temp 0.2
Assert ($def.temperature -eq 0.2)        "default: temperature = -Temp"
Assert (-not $def.ContainsKey('top_p'))  "default: no GLM sampling knobs"
Assert ($def.model -eq "coder-fast")     "carries the model id"
Assert ($def.tools.Count -eq 1)          "carries the tools array"
$req = New-ToolReq -Model "m" -Messages $umsgs -Tools @($tool) -ToolChoice "required" -Temp 0.2
Assert ($req.tool_choice -eq "required") "sets tool_choice when provided"
$glm = New-ToolReq -Model "m" -Messages $umsgs -Tools @($tool) -Temp 0.2 -GlmSampling
Assert ($glm.temperature -eq 0.7)        "GlmSampling: temperature 0.7"
Assert ($glm.top_p -eq 1.0)              "GlmSampling: top_p 1.0"
Assert ($glm.repeat_penalty -eq 1.0)     "GlmSampling: repeat_penalty 1.0 (GLM loops with the default)"
Assert ($glm.min_p -eq 0.01)             "GlmSampling: min_p 0.01"

Write-Host "`nRead-ToolResp (parse the OpenAI/llama.cpp response shape)"
$toolResp = [pscustomobject]@{ choices = @([pscustomobject]@{ message = [pscustomobject]@{ content = $null; tool_calls = @([pscustomobject]@{ id = "c1"; function = [pscustomobject]@{ name = "get_weather"; arguments = '{"city":"Tokyo"}' } }) } }) }
$p = Read-ToolResp $toolResp
Assert ($p.HasToolCall)            "detects a tool_call"
Assert ($p.Name -eq "get_weather") "extracts the tool name"
Assert ($p.Args -match 'Tokyo')    "extracts the tool arguments"
$finalResp = [pscustomobject]@{ choices = @([pscustomobject]@{ message = [pscustomobject]@{ content = "18C clear"; tool_calls = $null } }) }
$f = Read-ToolResp $finalResp
Assert (-not $f.HasToolCall)        "no tool_call on a plain final answer"
Assert ($f.FinalText -eq "18C clear") "extracts the final text"

Write-Host "`nAdd-ToolResultTurns (multi-hop transcript shaping)"
$asst = [pscustomobject]@{ content = $null }
$calls = @([pscustomobject]@{ id = "c1"; function = [pscustomobject]@{ name = "get_weather"; arguments = '{}' } })
$next = Add-ToolResultTurns -Messages $umsgs -AssistantMsg $asst -Calls $calls -ResultJson '{"temp_c":18}'
Assert ($next.Count -eq 3)             "1 user turn -> 3 (echo assistant + 1 tool result)"
Assert ($next[1].role -eq "assistant") "echoes the assistant turn"
Assert ($next[1].tool_calls.Count -eq 1) "assistant turn carries the tool_calls"
Assert ($next[2].role -eq "tool")      "appends a tool result turn"
Assert ($next[2].tool_call_id -eq "c1") "tool turn references the call id"
Assert ($next[2].content -match '18')  "tool turn carries the result json"

Write-Host "`n--- $($script:pass) passed, $($script:fail) failed ---"
if ($script:fail -gt 0) { exit 1 }
exit 0
