using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DokiDex.Web;

// Thin client for the local instruct model (llama-swap, OpenAI-compatible /v1/chat/completions on :8080 — up
// in agent/coexist mode). Shared by every llm-orchestration surface (Director storyboarding, the steerable
// rewriter, …) so the chat call + the "not reachable, start agent mode" degradation live in ONE place.
public static class LocalLlm
{
    private const string ChatUrl = "http://127.0.0.1:8080/v1/chat/completions";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    // A DEDICATED client for STREAMING only: HttpClient.Timeout spans the WHOLE streamed operation in .NET (not
    // just the headers), so a long generation would trip the 3-min shared-client timeout mid-stream. The stream
    // is bounded instead by the request CancellationToken (browser abort cancels the upstream read).
    private static readonly HttpClient StreamHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public sealed record ChatResult(bool Ok, string Text, string? Error);

    // ONE parsed tool call from choices[0].message.tool_calls[]. ArgumentsJson is the RAW function.arguments
    // string (an OpenAI tool-call carries arguments as a JSON STRING, not an object), passed verbatim to the
    // tool executor (ChatTools.Run). Id is the upstream tool_call_id echoed back on the role:"tool" result turn.
    public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

    // The tool-calling result of ChatToolsAsync: BOTH the assistant content (may be empty when the model chose a
    // tool instead of answering) AND the parsed tool_calls (empty when the model just answered). Ok=false +
    // Error mirrors ChatResult's degradation (LLM down / no model) so the agent loop ends gracefully.
    public sealed record ToolChatResult(bool Ok, string Content, IReadOnlyList<ToolCall> ToolCalls, string? Error);

    // PURE: extract choices[0].message.tool_calls[].{id, function.name, function.arguments} from a llama-swap
    // /v1/chat/completions response. The exact shape is proven by serving/test-toolcall.ps1
    // (choices[0].message.tool_calls[].function.{name,arguments}). Returns EMPTY for: a plain content reply with
    // no tool_calls (the graceful fallthrough — that content is the answer), an empty tool_calls array, and any
    // malformed/partial JSON. A call with no function.name is skipped (cannot be dispatched); missing arguments
    // (or a non-STRING arguments value some open models emit, e.g. a JSON object) default to "{}". A missing id is
    // SYNTHESIZED as a unique "call_<index>" — never "" — so two id-less calls in one hop don't collide on the same
    // tool_call_id when the loop echoes the role:"tool" results. Total + side-effect-free — unit-tested like ParseSseDelta.
    public static IReadOnlyList<ToolCall> ParseToolCalls(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return Array.Empty<ToolCall>();
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return Array.Empty<ToolCall>();
            if (!choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("tool_calls", out var toolCalls)
                || toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() == 0)
                return Array.Empty<ToolCall>();

            var list = new List<ToolCall>();
            var index = 0;
            foreach (var tc in toolCalls.EnumerateArray())
            {
                var slot = index++;   // positional index of THIS entry (used to synthesize an id when absent)
                if (!tc.TryGetProperty("function", out var fn)) continue;
                if (!fn.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // A missing/blank/non-string id is replaced with a synthesized unique id so id-less calls can't
                // collide on tool_call_id "" (the loop relies on the id to correlate each role:"tool" result).
                var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? (idEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(id)) id = $"call_{slot}";

                // OpenAI carries arguments as a JSON STRING; a non-string value (e.g. an object some open models
                // emit) is NOT a valid arguments payload here, so fall back to "{}" rather than mis-reading it.
                var args = fn.TryGetProperty("arguments", out var argEl) && argEl.ValueKind == JsonValueKind.String
                    ? argEl.GetString() ?? "{}" : "{}";
                if (string.IsNullOrWhiteSpace(args)) args = "{}";

                list.Add(new ToolCall(id, name!, args));
            }
            return list;
        }
        catch { return Array.Empty<ToolCall>(); }
    }

    // Tool-calling multi-turn chat (agent loop, non-streaming): the SAME array-based Body as ChatTurnsAsync plus a
    // 'tools' array + tool_choice:"auto", returning BOTH the assistant content AND the parsed tool_calls. The
    // open-model risk (some models emit a TEXTUAL <function=...> rather than real tool_calls) is mitigated upstream
    // by ParseToolCalls returning empty for any non-tool_calls reply => the agent loop treats that content as the
    // final answer. Degrades exactly like PostAsync: LLM down / no model => Ok=false + the canonical message.
    public static async Task<ToolChatResult> ChatToolsAsync(
        IReadOnlyList<object> messages, object toolsJson, double temperature, int maxTokens,
        CancellationToken ct, string? model = null)
    {
        var body = Body(messages.ToArray(), temperature, maxTokens, model);
        body["tools"] = toolsJson;
        body["tool_choice"] = "auto";
        try
        {
            using var resp = await Http.PostAsJsonAsync(ChatUrl, body, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new ToolChatResult(false, "", Array.Empty<ToolCall>(),
                    $"LLM returned {(int)resp.StatusCode} — is a model loaded? (start agent mode)");
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var toolCalls = ParseToolCalls(raw);
            string content = "";
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString() ?? "";
            }
            catch { /* content stays "" — tool_calls already parsed above */ }
            return new ToolChatResult(true, content, toolCalls, null);
        }
        catch (OperationCanceledException) { return new ToolChatResult(false, "", Array.Empty<ToolCall>(), "cancelled"); }
        catch (Exception ex) { return new ToolChatResult(false, "", Array.Empty<ToolCall>(), $"LLM not reachable at :8080 — start agent mode first ({ex.Message})"); }
    }

    // `model` (optional) selects which llama-swap model serves the request — the speed/quality TIER (see
    // LlmTiers). Null omits the field entirely, preserving the pre-tier behavior (llama-swap's loaded default).
    public static Task<ChatResult> ChatAsync(string system, string user, double temperature, int maxTokens, CancellationToken ct, string? model = null)
        => PostAsync(Body(new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user },
        }, temperature, maxTokens, model), ct);

    // Multi-turn chat over a prebuilt OpenAI message[] (system bundle + history + user turn, assembled by the
    // pure ChatPrompt.Build). A thin wrapper over the SAME array-based Body the one-shot ChatAsync uses, so the
    // "not reachable, start agent mode" degradation lives in the one PostAsync. The one-shot ChatAsync/
    // ChatVisionAsync stay untouched (Director/Rewriter/Vision/multichar keep working).
    public static Task<ChatResult> ChatTurnsAsync(IReadOnlyList<object> messages, double temperature, int maxTokens, CancellationToken ct, string? model = null)
        => PostAsync(Body(messages.ToArray(), temperature, maxTokens, model), ct);

    // STREAMING multi-turn chat (P2): the SAME array-based Body as ChatTurnsAsync but with stream=true, sent with
    // HttpCompletionOption.ResponseHeadersRead on the dedicated infinite-timeout StreamHttp, reading llama-swap's
    // upstream OpenAI SSE response line by line and yielding each non-null content delta (parsed by the pure
    // ParseSseDelta). Degrades gracefully like PostAsync: an LLM-down / connect error simply ends the sequence
    // (yields nothing) — the caller observes zero deltas and surfaces the canonical "start agent mode" message.
    public static async IAsyncEnumerable<string> ChatStreamAsync(
        IReadOnlyList<object> messages, double temperature, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct, string? model = null)
    {
        var body = Body(messages.ToArray(), temperature, maxTokens, model);
        body["stream"] = true;

        HttpResponseMessage? resp = null;
        Stream? stream = null;
        StreamReader? reader = null;
        try
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
                {
                    Content = JsonContent.Create(body),
                };
                resp = await StreamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) yield break;   // LLM up but no model loaded => no deltas (the finally disposes resp)
                stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                reader = new StreamReader(stream);
            }
            catch (OperationCanceledException) { yield break; }
            catch { yield break; }   // connect error / LLM down — degrade to an empty sequence

            while (true)
            {
                string? line;
                // Cancellable read: the parameterless ReadLineAsync() ignores ct, so a wedged llama-swap (bytes
                // stop without the socket closing) would block forever and a browser abort could not unstick it.
                // The ct overload (net7+) makes ctx.RequestAborted actually bound the read (Risk #3 in the design).
                try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                catch { yield break; }
                if (line is null) break;          // end of stream

                var delta = ParseSseDelta(line);
                if (delta is { Length: > 0 }) yield return delta;
            }
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
            resp?.Dispose();
        }
    }

    // PURE: extract choices[0].delta.content from ONE upstream OpenAI SSE line. Returns null for the '[DONE]'
    // sentinel, any blank/keepalive/non-'data:' line, and a delta with no content (e.g. the role-only first
    // chunk). Token content (quotes/newlines) comes back verbatim. Unit-tested like Director.ParseShotlist.
    // Also checks delta.reasoning_content as a fallback: gpt-oss-20b (the "reasoning" tier) streams its
    // chain-of-thought there instead of content, so we surface that text rather than yielding silence.
    public static string? ParseSseDelta(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        line = line.Trim();
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;

        var payload = line["data:".Length..].Trim();
        if (payload.Length == 0 || payload == "[DONE]") return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            if (!choices[0].TryGetProperty("delta", out var delta)) return null;
            // Primary: delta.content (all models, incl. standard text tokens from the reasoning model)
            if (delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                var s = content.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            // Fallback: delta.reasoning_content (gpt-oss-20b "reasoning" tier streams CoT here)
            if (delta.TryGetProperty("reasoning_content", out var rc)
                && rc.ValueKind == JsonValueKind.String)
            {
                var s = rc.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }
        catch { return null; }
    }

    // Multimodal chat: a text instruction + one image (as a data: URL), in the OpenAI image_url content shape.
    // GATED — it succeeds only when the loaded llama-swap model is vision-capable; otherwise it degrades through
    // the same not-reachable/error path as text chat. Powers Describe (image->prompt) and output verification.
    // Pass `model` = LlmTiers.Vision so llama-swap loads the dedicated vision block.
    public static Task<ChatResult> ChatVisionAsync(string system, string userText, string imageDataUrl, double temperature, int maxTokens, CancellationToken ct, string? model = null)
        => PostAsync(Body(new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = new object[]
            {
                new { type = "text", text = userText },
                new { type = "image_url", image_url = new { url = imageDataUrl } },
            }},
        }, temperature, maxTokens, model), ct);

    // Build the request body, including the OpenAI "model" field only when a tier model is named.
    private static Dictionary<string, object?> Body(object[] messages, double temperature, int maxTokens, string? model)
    {
        var b = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };
        if (!string.IsNullOrWhiteSpace(model)) b["model"] = model;
        return b;
    }

    private static async Task<ChatResult> PostAsync(object body, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.PostAsJsonAsync(ChatUrl, body, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new ChatResult(false, "", $"LLM returned {(int)resp.StatusCode} — is a model loaded? (start agent mode)");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var text = msg.GetProperty("content").GetString() ?? "";
            // gpt-oss-20b (the "reasoning" tier) puts chain-of-thought in reasoning_content and returns a
            // brief or empty visible content. Fall back to reasoning_content so the reply is never blank.
            if (string.IsNullOrWhiteSpace(text)
                && msg.TryGetProperty("reasoning_content", out var rc)
                && rc.ValueKind == JsonValueKind.String)
                text = rc.GetString() ?? "";
            return new ChatResult(true, text, null);
        }
        catch (OperationCanceledException) { return new ChatResult(false, "", "cancelled"); }
        catch (Exception ex) { return new ChatResult(false, "", $"LLM not reachable at :8080 — start agent mode first ({ex.Message})"); }
    }
}
