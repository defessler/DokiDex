namespace DokiDex.Web;

// The user-chosen speed/quality TIER for the latency-tolerant LLM workflows (Director, pitch-deck, multi-
// character phrasing). A tier is just a llama-swap MODEL NAME: the request names a tier, Resolve maps it to a
// model, and LocalLlm sends that model so llama-swap loads it. The rewriter (:8013) and FIM (:8012) are
// deliberately NOT tiered — they must stay fast.
//
//   Fast    -> coder-fast (Qwen3-Coder-30B-A3B, fully on GPU, interactive)   [default]
//   Quality -> coder-big  (gpt-oss-120b, GPU+CPU hybrid offload, slower but stronger)
//
// Resolve is pure + total -> unit-tested. A null result means "send no model" = leave whatever llama-swap has
// loaded (the pre-tier behavior), so callers degrade to the existing default when no tier is chosen.
public static class LlmTiers
{
    public const string Fast = "coder-fast";       // must match a model name in serving/llama-swap.yaml
    public const string Quality = "coder-big";
    public const string Vision = "vision";         // the gated vision block (Describe/Verify) in llama-swap.yaml

    public static string? Resolve(string? tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "fast" or "draft" or "quick"       => Fast,
        "quality" or "slow" or "max" or "best" => Quality,
        _ => null,   // unknown/empty -> no model field -> llama-swap's currently-loaded default
    };
}
