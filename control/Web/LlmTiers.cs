using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// The user-chosen speed/quality TIER for the latency-tolerant LLM workflows (Director, pitch-deck, multi-
// character phrasing). A tier is just a llama-swap MODEL NAME: the request names a tier, Resolve maps it to a
// model, and LocalLlm sends that model so llama-swap loads it. The rewriter (:8013) and FIM (:8012) are
// deliberately NOT tiered — they must stay fast.
//
// Roles (text speed-picker options):
//   fast      -> coder-fast (Qwen3-Coder-30B-A3B, fully on GPU, interactive)   [default]
//   quality   -> coder-big  (gpt-oss-120b, GPU+CPU hybrid offload, slower but stronger)
//   reasoning -> fast-candidate-gptoss20b (gpt-oss-20b, chain-of-thought reasoning via reasoning_content)
//
// vision -> "vision" stays resolvable but is NOT a speed-picker role — it's auto-applied on image attach.
//
// Resolve is pure + total -> unit-tested. A null result means "send no model" = leave whatever llama-swap has
// loaded (the pre-tier behavior), so callers degrade to the existing default when no tier is chosen.
//
// Available(configuredModels) is a pure filter: text speed roles whose model is in the configured model list
// (from StatusProbe.LlamaSwapInfoAsync / /v1/models). Callers use this to advertise only usable tiers.
public static class LlmTiers
{
    public const string Fast = "coder-fast";                   // must match a model name in serving/llama-swap.yaml
    public const string Quality = "coder-big";
    public const string Reasoning = "fast-candidate-gptoss20b"; // gpt-oss-20b; chain-of-thought in reasoning_content
    public const string Vision = "vision";                     // the gated vision block (Describe/Verify) in llama-swap.yaml

    // The text speed-picker roles exposed to the user. Vision is EXCLUDED — it is auto-applied on image attach,
    // not a speed option the user selects. Each role: (id, label, model-name used in llama-swap).
    private static readonly IReadOnlyList<(string Id, string Label, string Model)> TextRoles = new[]
    {
        ("fast",      "fast",             Fast),
        ("quality",   "quality · slower", Quality),
        ("reasoning", "reasoning",        Reasoning),
    };

    // Pure: returns the subset of text speed roles whose model name is in configuredModels (i.e. those that
    // llama-swap actually has configured and will accept). The SPA uses this to show only available tiers.
    public static IReadOnlyList<(string Id, string Label)> Available(IEnumerable<string> configuredModels)
    {
        var set = new HashSet<string>(configuredModels, System.StringComparer.OrdinalIgnoreCase);
        return TextRoles
            .Where(r => set.Contains(r.Model))
            .Select(r => (r.Id, r.Label))
            .ToList();
    }

    public static string? Resolve(string? tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "fast" or "draft" or "quick"            => Fast,
        "quality" or "slow" or "max" or "best"  => Quality,
        "reasoning"                             => Reasoning,
        _ => null,   // unknown/empty -> no model field -> llama-swap's currently-loaded default
    };
}
