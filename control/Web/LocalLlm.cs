using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
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

                // OpenAI carries arguments as a JSON STRING. A non-string value is a known open/local-model quirk
                // (Qwen3-Coder and friends often emit an object, not the escaped string):
                //   • a JSON OBJECT (e.g. {"query":"x"}) carries the REAL args — serialize it back to a JSON string
                //     so the downstream arg-parsers (ParseQuery / MapGenArgs / …) recover the model's intent.
                //     The old "{}" fallback silently DROPPED the call's arguments (search_library would list the
                //     most recent items instead of searching the terms the model asked for) — a real reliability bug.
                //   • any other non-string (array / number / bool / null) is not a usable args object => "{}".
                string args;
                if (fn.TryGetProperty("arguments", out var argEl))
                    args = argEl.ValueKind switch
                    {
                        JsonValueKind.String => argEl.GetString() ?? "{}",
                        JsonValueKind.Object => argEl.GetRawText(),
                        _ => "{}",
                    };
                else
                    args = "{}";
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
        // Tool calls run TIGHTER sampling than free-form chat: low temp (passed in by Chat.ToolTemperature) plus
        // min_p 0.1 + top_p 0.9 — the research §5.4 tool-calling profile. Min-p adapts to model confidence and cuts
        // tool-format drift more reliably than top-p alone; the conversational paths keep their temp-0.8 default.
        var body = Body(messages.ToArray(), temperature, maxTokens, model, minP: 0.1, topP: 0.9);
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

    // STREAMING tool-calling multi-turn chat (1.1): the SAME body as ChatToolsAsync (tools + tool_choice:"auto" +
    // the tightened tool-call sampling) plus stream=true, sent on the dedicated infinite-timeout StreamHttp with
    // HttpCompletionOption.ResponseHeadersRead — mirrors ChatStreamAsync's request/read/dispose pattern including
    // the cancellable ReadLineAsync(ct). Each SSE data-line's JSON payload is fed to a fresh, PURE
    // ToolCallStreamAccumulator: delta.content chunks are both accumulated AND handed to onToken live (so the
    // terminal can paint tokens as they arrive); delta.reasoning_content is dropped entirely — gpt-oss chain-of-
    // thought must never reach the transcript (F1a); delta.tool_calls[] fragments accumulate index-keyed. At
    // stream end (the '[DONE]' sentinel, or EOF with none) the accumulator is finished into the same ToolCall
    // shape ParseToolCalls produces (nameless calls dropped, missing id synthesized, blank args -> "{}").
    // FALLBACK (F1c, cheap insurance): a non-success status, a transport error at any point, or any accumulated
    // function.arguments that is non-blank yet fails to parse as JSON all fall back to ONE blocking ChatToolsAsync
    // call — never a bare error when the blocking path might still answer fine. Cancellation returns
    // (false,"",[],"cancelled") exactly like the blocking path.
    public static async Task<ToolChatResult> ChatToolsStreamAsync(
        IReadOnlyList<object> messages, object toolsJson, double temperature, int maxTokens,
        Action<string> onToken, CancellationToken ct, string? model = null)
    {
        var body = Body(messages.ToArray(), temperature, maxTokens, model, minP: 0.1, topP: 0.9);
        body["tools"] = toolsJson;
        body["tool_choice"] = "auto";
        body["stream"] = true;

        var acc = new ToolCallStreamAccumulator();
        var needsFallback = false;

        HttpResponseMessage? resp = null;
        Stream? stream = null;
        StreamReader? reader = null;
        try
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl) { Content = JsonContent.Create(body) };
                resp = await StreamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) needsFallback = true;   // LLM up but no model loaded, etc. — retry blocking
                else
                {
                    stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    reader = new StreamReader(stream);
                }
            }
            catch (OperationCanceledException) { return new ToolChatResult(false, "", Array.Empty<ToolCall>(), "cancelled"); }
            catch { needsFallback = true; }   // connect error / LLM down — retry once, blocking

            if (!needsFallback && reader is not null)
            {
                while (true)
                {
                    string? line;
                    // Cancellable read (net7+ ct overload) — same reasoning as ChatStreamAsync: a wedged
                    // llama-swap must not block forever past a cancellation request.
                    try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return new ToolChatResult(false, "", Array.Empty<ToolCall>(), "cancelled"); }
                    catch { needsFallback = true; break; }
                    if (line is null) break;   // EOF with no [DONE] — treat like end of stream

                    line = line.Trim();
                    if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var payload = line["data:".Length..].Trim();
                    if (payload.Length == 0) continue;
                    if (payload == "[DONE]") break;

                    // NB: if a tool-call hop streams some prose alongside a tool call that later turns out
                    // malformed, that prose may already be painted before the fallback below discards it and
                    // retries blocking — a rare edge case (tool-call hops rarely interleave real content)
                    // accepted for this leaf rather than over-engineering a rollback of painted terminal output.
                    var chunk = acc.Push(payload);
                    if (!string.IsNullOrEmpty(chunk)) onToken(chunk);
                }
            }
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
            resp?.Dispose();
        }

        if (needsFallback)
            return await ChatToolsAsync(messages, toolsJson, temperature, maxTokens, ct, model).ConfigureAwait(false);

        var (content, calls, _) = acc.Finish();
        if (acc.HasMalformedArguments)
            return await ChatToolsAsync(messages, toolsJson, temperature, maxTokens, ct, model).ConfigureAwait(false);

        return new ToolChatResult(true, content, calls, null);
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

    // Build the request body, including the OpenAI "model" field only when a tier model is named. minP/topP are
    // OPTIONAL extra sampling params: null (the default for every chat/vision/director/rewriter caller) omits them
    // entirely, so those requests stay byte-for-byte as before; the tool-calling path (ChatToolsAsync) passes them
    // to TIGHTEN sampling for reliable tool selection (research §5.4: tool calls want low temp + min_p, not the
    // conversational temp 0.8). Internal so the "tools tighten, chat untouched" invariant is unit-testable.
    internal static Dictionary<string, object?> Body(object[] messages, double temperature, int maxTokens, string? model,
        double? minP = null, double? topP = null)
    {
        var b = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };
        if (!string.IsNullOrWhiteSpace(model)) b["model"] = model;
        if (minP is { } mp) b["min_p"] = mp;
        if (topP is { } tp) b["top_p"] = tp;
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

// PURE accumulator for ONE streamed tool-calling turn (1.1): fed the JSON payload of each upstream SSE 'data:'
// line (already stripped of the "data:" prefix and the '[DONE]' sentinel by the caller, LocalLlm.ChatToolsStreamAsync),
// it accumulates delta.content into the final answer text, delta.tool_calls[] fragments index-keyed (id/name set
// on first sight, function.arguments concatenated in arrival order), and the latest finish_reason. delta.
// reasoning_content is read NEVER — gpt-oss's chain-of-thought must not poison the transcript (F1a); a dimmed
// display can come later without touching this accumulator. No network/disk — unit-tested like ParseSseDelta/
// ParseToolCalls (ToolCallStreamAccumulatorTests).
internal sealed class ToolCallStreamAccumulator
{
    private readonly StringBuilder _content = new();
    private readonly Dictionary<int, (string? Id, string? Name, StringBuilder Args)> _calls = new();
    private string? _finishReason;

    // Set by Finish(): true when some accumulated function.arguments is non-blank yet not valid JSON — the
    // caller's signal to fall back to a blocking retry (F1c) rather than dispatch a tool call with broken args.
    public bool HasMalformedArguments { get; private set; }

    // Feed one SSE data-line PAYLOAD. Returns the content delta to display live (onToken), or null when this
    // line carried no displayable content (a tool-call fragment, reasoning_content, a role/finish-only chunk, or
    // unparseable JSON — all handled the same defensive way as ParseSseDelta, but total: never throws).
    public string? Push(string sseDataPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(sseDataPayloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(sseDataPayloadJson);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                var s = fr.GetString();
                if (!string.IsNullOrEmpty(s)) _finishReason = s;
            }

            if (!choice.TryGetProperty("delta", out var delta)) return null;

            string? chunk = null;
            if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                var s = contentEl.GetString();
                if (!string.IsNullOrEmpty(s)) { _content.Append(s); chunk = s; }
            }
            // delta.reasoning_content: intentionally NOT read. Dropping it here (rather than routing it to
            // `chunk`/_content like ParseSseDelta's fallback does for the non-tool chat path) is the F1a fix —
            // gpt-oss CoT must never land in the transcript that gets replayed back to the model or the user.

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    if (!tc.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number) continue;
                    var idx = idxEl.GetInt32();
                    if (!_calls.TryGetValue(idx, out var entry)) entry = (null, null, new StringBuilder());

                    var id = entry.Id;
                    if (id is null && tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        id = idEl.GetString();

                    var name = entry.Name;
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (name is null && fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            name = nameEl.GetString();
                        if (fn.TryGetProperty("arguments", out var argEl) && argEl.ValueKind == JsonValueKind.String)
                            entry.Args.Append(argEl.GetString());
                    }
                    _calls[idx] = (id, name, entry.Args);
                }
            }
            return chunk;
        }
        catch { return null; }
    }

    // End of stream: turn the accumulated fragments into the same ToolCall shape ParseToolCalls produces — SAME
    // hygiene rules (nameless entries skipped; a missing/blank id synthesized as "call_<index>"; blank/absent
    // arguments default to "{}") — plus set HasMalformedArguments when a non-blank arguments string still isn't
    // valid JSON once fully concatenated (checked ONLY here, at the end — a fragment is routinely partial/
    // invalid JSON mid-stream, so checking earlier would misfire on every normal multi-chunk call).
    public (string Content, IReadOnlyList<LocalLlm.ToolCall> Calls, string? FinishReason) Finish()
    {
        var calls = new List<LocalLlm.ToolCall>();
        var malformed = false;
        foreach (var (idx, entry) in _calls.OrderBy(kv => kv.Key))
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var id = string.IsNullOrWhiteSpace(entry.Id) ? $"call_{idx}" : entry.Id!;
            var args = entry.Args.ToString();
            if (string.IsNullOrWhiteSpace(args)) args = "{}";
            else
            {
                try { using var _ = JsonDocument.Parse(args); }
                catch { malformed = true; }
            }
            calls.Add(new LocalLlm.ToolCall(id, entry.Name!, args));
        }
        HasMalformedArguments = malformed;
        return (_content.ToString(), calls, _finishReason);
    }
}
