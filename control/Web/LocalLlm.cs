using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace DokiDex.Web;

// Thin client for the local instruct model (llama-swap, OpenAI-compatible /v1/chat/completions on :8080 — up
// in agent/coexist mode). Shared by every llm-orchestration surface (Director storyboarding, the steerable
// rewriter, …) so the chat call + the "not reachable, start agent mode" degradation live in ONE place.
public static class LocalLlm
{
    private const string ChatUrl = "http://127.0.0.1:8080/v1/chat/completions";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public sealed record ChatResult(bool Ok, string Text, string? Error);

    // `model` (optional) selects which llama-swap model serves the request — the speed/quality TIER (see
    // LlmTiers). Null omits the field entirely, preserving the pre-tier behavior (llama-swap's loaded default).
    public static Task<ChatResult> ChatAsync(string system, string user, double temperature, int maxTokens, CancellationToken ct, string? model = null)
        => PostAsync(Body(new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user },
        }, temperature, maxTokens, model), ct);

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
