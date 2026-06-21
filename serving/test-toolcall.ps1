# Phase-1 acceptance test: native tool-calling + decode speed per model.
# Usage: .\test-toolcall.ps1 -Model coder-fast
#        .\test-toolcall.ps1 -Model coder-candidate-glm -GlmSampling   # GLM-class: temp 0.7 / top_p 1 / repeat-penalty 1 / min_p 0.01
#
# Tests: (1) auto tool-call, (1b) FORCED tool_choice=required, (2) decode speed, (3) multi-hop loop terminates.
# 1b + 3 exist to catch the GLM-4.7-Flash failure mode (llama.cpp #19009: ignores tool_choice / loops
# forever in required+thinking mode) that a single auto-mode call cannot surface. Zero tool-call flakes is the gate.
# The pure request/parse/transcript logic is factored into helpers + unit-tested offline (tests/test-toolcall.test.ps1).
param(
    [string]$Model = "coder-fast",
    [string]$Endpoint = "http://127.0.0.1:8080",
    [double]$ToolTemp = 0.2,    # sampling for the tool-call tests (overridden by -GlmSampling)
    [int]$MaxHops = 4,          # multi-hop loop ceiling (matches the chat agent loop); exceeding it = infinite-loop FAIL
    [switch]$GlmSampling        # GLM/unsloth-recommended sampling for the tool tests (temp 0.7, top_p 1.0, repeat_penalty 1.0, min_p 0.01)
)

$ErrorActionPreference = "Stop"

# ---- pure helpers (unit-tested by tests/test-toolcall.test.ps1) ------------
function New-ToolReq {
    # Build a chat-completions request body (hashtable). -GlmSampling applies the GLM/unsloth
    # recommended sampling (GLM derails/loops on the default penalty); otherwise just -Temp.
    param(
        [string]$Model,
        [array]$Messages,
        [array]$Tools,
        [string]$ToolChoice,
        [double]$Temp = 0.2,
        [switch]$GlmSampling
    )
    $req = @{ model = $Model; messages = $Messages }
    if ($Tools)      { $req.tools = $Tools }
    if ($ToolChoice) { $req.tool_choice = $ToolChoice }
    if ($GlmSampling) {
        $req.temperature    = 0.7
        $req.top_p          = 1.0
        $req.repeat_penalty = 1.0   # llama.cpp extension
        $req.min_p          = 0.01
    } else {
        $req.temperature = $Temp
    }
    return $req
}

function Read-ToolResp {
    # Normalize an OpenAI/llama.cpp chat response to the fields the tests care about.
    param($Resp)
    $msg   = $Resp.choices[0].message
    $calls = $msg.tool_calls
    $tname = if ($calls) { $calls[0].function.name } else { $null }
    $targs = if ($calls) { $calls[0].function.arguments } else { $null }
    [pscustomobject]@{
        HasToolCall = [bool]$calls
        Name        = $tname
        Args        = $targs
        FinalText   = $msg.content
        Calls       = $calls
    }
}

function Add-ToolResultTurns {
    # Shape the next hop: echo the assistant tool-call turn, then one tool-result turn per call.
    param([array]$Messages, $AssistantMsg, [array]$Calls, [string]$ResultJson)
    $out = @($Messages)
    $out += @{ role = "assistant"; content = $AssistantMsg.content; tool_calls = $Calls }
    foreach ($c in $Calls) {
        $out += @{ role = "tool"; tool_call_id = $c.id; content = $ResultJson }
    }
    return ,$out
}

function Invoke-Chat {
    param([hashtable]$Req)
    $body = $Req | ConvertTo-Json -Depth 12
    return Invoke-RestMethod -Uri "$Endpoint/v1/chat/completions" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 1200
}

# ---- shared get_weather tool ----------------------------------------------
$weatherTool = @{
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
}

$failures = 0

# --- Test 1: auto tool call -------------------------------------------------
$req1 = New-ToolReq -Model $Model -Messages @(@{ role = "user"; content = "What is the weather in Tokyo right now? Use the tool." }) -Tools @($weatherTool) -Temp $ToolTemp -GlmSampling:$GlmSampling
Write-Host "[$Model] T1 auto tool call (model may need to load first)..."
$t0 = Get-Date
$resp1 = Invoke-Chat $req1
$loadAndCall = ((Get-Date) - $t0).TotalSeconds
$r1 = Read-ToolResp $resp1
if ($r1.HasToolCall -and $r1.Name -eq "get_weather") {
    Write-Host "T1 TOOLCALL OK  name=$($r1.Name) args=$($r1.Args)  (turn took $([math]::Round($loadAndCall,1))s)"
} else {
    Write-Host "T1 TOOLCALL FAIL - no valid tool_calls in response:"; $resp1.choices[0].message | ConvertTo-Json -Depth 6; $failures++
}

# --- Test 1b: FORCED tool call (tool_choice=required) -----------------------
# A compliant model emits a tool_call even when the prompt doesn't obviously need one. Models that
# ignore tool_choice (GLM-4.7-Flash, llama.cpp #19009) fail here.
$req1b = New-ToolReq -Model $Model -Messages @(@{ role = "user"; content = "Say hello to the user." }) -Tools @($weatherTool) -ToolChoice "required" -Temp $ToolTemp -GlmSampling:$GlmSampling
Write-Host "[$Model] T1b forced tool_choice=required ..."
$r1b = Read-ToolResp (Invoke-Chat $req1b)
if ($r1b.HasToolCall) {
    Write-Host "T1b REQUIRED OK  forced call=$($r1b.Name)"
} else {
    Write-Host "T1b REQUIRED FAIL - model ignored tool_choice=required (no tool_calls)"; $failures++
}

# --- Test 2: decode speed ---------------------------------------------------
$genReq = New-ToolReq -Model $Model -Messages @(@{ role = "user"; content = "Write a detailed explanation of how a B-tree works, about 400 words." }) -Temp $ToolTemp -GlmSampling:$GlmSampling
$genReq.max_tokens = 500
Write-Host "[$Model] T2 measuring decode speed (500 tokens)..."
$resp2 = Invoke-Chat $genReq
if ($resp2.timings) {
    $pp = [math]::Round($resp2.timings.prompt_per_second, 1)
    $tg = [math]::Round($resp2.timings.predicted_per_second, 1)
    Write-Host "T2 SPEED  prefill=$pp tok/s  decode=$tg tok/s  (n=$($resp2.usage.completion_tokens))"
} else {
    Write-Host "T2 SPEED  (no timings field; completion_tokens=$($resp2.usage.completion_tokens))"
}

# --- Test 3: multi-hop tool loop must TERMINATE -----------------------------
# Ask for weather -> tool_call; return a synthetic result; the model must produce a FINAL answer
# within $MaxHops. Catches the GLM infinite-loop where it re-calls the tool forever (#19009).
$msgs = @(@{ role = "user"; content = "What's the weather in Paris? Use the tool, then answer in one sentence." })
$hop = 0; $finalText = $null
while ($hop -lt $MaxHops) {
    $hop++
    $r = Read-ToolResp (Invoke-Chat (New-ToolReq -Model $Model -Messages $msgs -Tools @($weatherTool) -Temp $ToolTemp -GlmSampling:$GlmSampling))
    if ($r.HasToolCall) {
        $msgs = Add-ToolResultTurns -Messages $msgs -AssistantMsg $r -Calls $r.Calls -ResultJson '{"city":"Paris","temp_c":18,"sky":"clear"}'
    } else {
        $finalText = $r.FinalText; break
    }
}
if ($finalText) {
    $snip = $finalText.Substring(0, [Math]::Min(80, $finalText.Length))
    Write-Host "T3 MULTIHOP OK  terminated in $hop hop(s); final: $snip"
} else {
    Write-Host "T3 MULTIHOP FAIL - no final answer within $MaxHops hops (looping?)"; $failures++
}

# --- Summary ----------------------------------------------------------------
if ($failures -gt 0) { Write-Host "RESULT: $failures tool-call test(s) FAILED for $Model"; exit 1 }
Write-Host "RESULT: all tool-call tests passed for $Model"
exit 0
