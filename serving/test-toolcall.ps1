# Phase-1 acceptance test: native tool-calling + decode speed per model.
# Usage: .\test-toolcall.ps1 -Model coder-fast
param([string]$Model = "coder-fast", [string]$Endpoint = "http://127.0.0.1:8080")

$ErrorActionPreference = "Stop"

# --- Test 1: tool call ------------------------------------------------------
$toolReq = @{
    model = $Model
    messages = @(@{ role = "user"; content = "What is the weather in Tokyo right now? Use the tool." })
    tools = @(@{
        type = "function"
        function = @{
            name = "get_weather"
            description = "Get current weather for a city"
            parameters = @{
                type = "object"
                properties = @{ city = @{ type = "string" } }
                required = @("city")
            }
        }
    })
    temperature = 0.2
} | ConvertTo-Json -Depth 10

Write-Host "[$Model] requesting tool call (model may need to load first)..."
$t0 = Get-Date
$resp = Invoke-RestMethod -Uri "$Endpoint/v1/chat/completions" -Method Post -ContentType "application/json" -Body $toolReq -TimeoutSec 1200
$loadAndCall = ((Get-Date) - $t0).TotalSeconds

$tc = $resp.choices[0].message.tool_calls
if ($tc -and $tc[0].function.name -eq "get_weather") {
    $args = $tc[0].function.arguments
    Write-Host "TOOLCALL OK  name=$($tc[0].function.name) args=$args  (turn took $([math]::Round($loadAndCall,1))s)"
} else {
    Write-Host "TOOLCALL FAIL — no valid tool_calls in response:"
    $resp.choices[0].message | ConvertTo-Json -Depth 6
    exit 1
}

# --- Test 2: decode speed ---------------------------------------------------
$genReq = @{
    model = $Model
    messages = @(@{ role = "user"; content = "Write a detailed explanation of how a B-tree works, about 400 words." })
    max_tokens = 500
    temperature = 0.2
} | ConvertTo-Json -Depth 5

Write-Host "[$Model] measuring decode speed (500 tokens)..."
$resp2 = Invoke-RestMethod -Uri "$Endpoint/v1/chat/completions" -Method Post -ContentType "application/json" -Body $genReq -TimeoutSec 1200
if ($resp2.timings) {
    $pp = [math]::Round($resp2.timings.prompt_per_second, 1)
    $tg = [math]::Round($resp2.timings.predicted_per_second, 1)
    Write-Host "SPEED  prefill=$pp tok/s  decode=$tg tok/s  (n=$($resp2.usage.completion_tokens))"
} else {
    Write-Host "SPEED  (no timings field; completion_tokens=$($resp2.usage.completion_tokens))"
}
