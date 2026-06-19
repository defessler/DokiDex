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
            if (!choices[0].TryGetProperty("delta", out var delta)
                || !delta.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
                return null;
            var s = content.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
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
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return new ChatResult(true, text, null);
        }
        catch (OperationCanceledException) { return new ChatResult(false, "", "cancelled"); }
        catch (Exception ex) { return new ChatResult(false, "", $"LLM not reachable at :8080 — start agent mode first ({ex.Message})"); }
    }
}
