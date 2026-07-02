using System.Collections.Generic;
using System.Text.Json;

namespace DokiDex.Web;

// Pure JSON-shape parsers backing `/status` (1.6 — folds in the old 1.8 /status per the F3 merge). `doki code`
// probes llama-swap DIRECTLY from the CLI (Program.cs's own short-timeout HttpClient GETs against
// http://127.0.0.1:8080/v1/models and http://127.0.0.1:8080/running) rather than shelling out to `doki status`,
// so only the PURE parsing lives here — mirrored minimally from StatusProbe.LlamaSwapInfoAsync (DokiDex.Control.
// Services, the WPF control plane's own equivalent probe) so the two shapes stay consistent without `doki code`
// taking a dependency on that assembly's StatusDoc model types. Total + side-effect-free: unit-tested with no
// network, no GPU, mirroring ParseToolCalls/ParseSseDelta's tested-core discipline.
public static class CodeStatus
{
    public sealed record RunningInfo(string? Model, string? State);

    // PURE: llama-swap's GET /running. The common shape is `{"running":[{"model":"...","state":"..."}]}` (an
    // array of currently-loaded models — doki code only ever runs one llama-swap group, so [0] is the one that
    // matters); some builds instead put the fields directly at the root. Returns (null, null) — never throws —
    // for an empty/absent `running` array, a non-object root, or unparseable JSON.
    public static RunningInfo ParseRunning(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new RunningInfo(null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var first = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("running", out var run) && run.ValueKind == JsonValueKind.Array && run.GetArrayLength() > 0
                ? run[0] : root;
            if (first.ValueKind != JsonValueKind.Object) return new RunningInfo(null, null);
            var model = first.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var state = first.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            return new RunningInfo(model, state);
        }
        catch { return new RunningInfo(null, null); }
    }

    // PURE: llama-swap's GET /v1/models — the OpenAI-shaped `{"data":[{"id":"coder-fast"}, ...]}` listing every
    // CONFIGURED tier (not just the loaded one). Entries with no string `id` are skipped. Returns an empty list
    // (never throws) for an absent/non-array `data`, a non-object root, or unparseable JSON.
    public static IReadOnlyList<string> ParseModels(string? json)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s)
                        list.Add(s);
        }
        catch { /* list stays whatever was parsed before the failure — total, never throws */ }
        return list;
    }
}
