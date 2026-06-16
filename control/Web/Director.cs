using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DokiDex.Web;

// One shot in a storyboard: an ordered title + a ready-to-generate image prompt.
public sealed record Shot(int Index, string Title, string Prompt);

// A browser request to storyboard an idea into N shots.
public sealed record DirectorRequest(string? Idea, int Shots = 6);

// The script-to-shotlist director. Turns a one-line idea or a script into an ordered list of shot prompts by
// asking the local instruct model (llama-swap, OpenAI-compatible /v1/chat/completions on :8080 — up in agent/
// coexist mode). The shotlist is plain TEXT, so it survives the GPU mode switch to media: storyboard in agent
// mode, then generate the shots in media mode (each shot is just an image gen through the existing pipeline).
//
// The fragile part — turning a chatty LLM reply into clean shots — is the pure, unit-tested ParseShotlist;
// the network call is a thin wrapper that degrades gracefully when the LLM is down (mirrors the SwarmGen
// "not reachable" contract).
public static class Director
{
    private const string ChatUrl = "http://127.0.0.1:8080/v1/chat/completions";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public sealed record Result(bool Ok, IReadOnlyList<Shot> Shots, string? Message);

    public static async Task<Result> StoryboardAsync(string idea, int shots, CancellationToken ct)
    {
        idea = (idea ?? "").Trim();
        if (idea.Length == 0) return new Result(false, Array.Empty<Shot>(), "empty idea");
        shots = Math.Clamp(shots, 1, 20);

        var sys = "You are a film director and storyboard artist. Break the user's idea into a sequence of "
                + "distinct, vivid camera shots. Reply with ONLY a JSON array (no prose, no code fence) of "
                + $"exactly {shots} objects, each {{\"title\": <a few words>, \"prompt\": <a detailed, standalone "
                + "image-generation prompt describing the shot: subject, setting, framing, lighting, mood>}}.";
        var body = new
        {
            messages = new[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = idea },
            },
            temperature = 0.7,
            max_tokens = 2048,
        };

        string text;
        try
        {
            using var resp = await Http.PostAsJsonAsync(ChatUrl, body, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Result(false, Array.Empty<Shot>(), $"LLM returned {(int)resp.StatusCode} — is a model loaded? (start agent mode)");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (OperationCanceledException) { return new Result(false, Array.Empty<Shot>(), "cancelled"); }
        catch (Exception ex) { return new Result(false, Array.Empty<Shot>(), $"LLM not reachable at :8080 — start agent mode first ({ex.Message})"); }

        var parsed = ParseShotlist(text);
        return parsed.Count == 0
            ? new Result(false, parsed, "the model did not return a usable shotlist — try again or rephrase")
            : new Result(true, parsed, null);
    }

    // Pure: turn an LLM reply into ordered shots. Robust to the usual mess — markdown ```json fences, leading
    // prose before the JSON, an object wrapper ({"shots":[...]}) vs a bare array, bare-string items, and
    // varied key names (title/name, prompt/description/text). Returns [] on anything unparseable.
    public static IReadOnlyList<Shot> ParseShotlist(string? llmText)
    {
        if (string.IsNullOrWhiteSpace(llmText)) return Array.Empty<Shot>();
        var json = ExtractJson(llmText);
        if (json is null) return Array.Empty<Shot>();

        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch { return Array.Empty<Shot>(); }

        // Accept a bare array, or an object whose first array property holds the shots ({"shots":[...]} etc.).
        JsonArray? arr = node as JsonArray;
        if (arr is null && node is JsonObject obj)
            arr = obj.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault();
        if (arr is null) return Array.Empty<Shot>();

        var shots = new List<Shot>();
        foreach (var item in arr)
        {
            string title = "", prompt = "";
            if (item is JsonObject o)
            {
                title  = Str(o, "title", "name", "heading", "shot") ?? "";
                prompt = Str(o, "prompt", "description", "desc", "text", "visual", "image") ?? "";
            }
            else if (item is JsonValue v && v.TryGetValue<string>(out var s))
            {
                prompt = s;   // a bare string item is the prompt
            }
            prompt = prompt.Trim();
            if (prompt.Length == 0) continue;
            shots.Add(new Shot(shots.Count + 1, title.Trim(), prompt));
        }
        return shots;
    }

    private static string? Str(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
            foreach (var kv in o)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)
                    && kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    return s;
        return null;
    }

    // Pull the first balanced JSON array/object out of free text (strips ``` fences + leading prose), tracking
    // string state so brackets inside string literals don't throw off the depth count.
    private static string? ExtractJson(string text)
    {
        // Prefer an array (a shotlist); fall back to an object wrapper.
        foreach (var (open, close) in new[] { ('[', ']'), ('{', '}') })
        {
            int start = text.IndexOf(open);
            if (start < 0) continue;
            int depth = 0; bool inStr = false, esc = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == open) depth++;
                else if (c == close) { depth--; if (depth == 0) return text.Substring(start, i - start + 1); }
            }
        }
        return null;
    }
}
