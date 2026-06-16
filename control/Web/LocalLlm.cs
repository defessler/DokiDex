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

    public static async Task<ChatResult> ChatAsync(string system, string user, double temperature, int maxTokens, CancellationToken ct)
    {
        var body = new
        {
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature,
            max_tokens = maxTokens,
        };
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
