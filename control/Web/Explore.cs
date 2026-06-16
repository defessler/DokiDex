namespace DokiDex.Web;

// A browser request to "explore" an idea: fan one prompt out into N distinct visual directions.
public sealed record ExploreRequest(
    string? Prompt, string? Kind = "image", int Count = 8, int Seed = -1,
    bool Fast = true, string? Aspect = null, string? Lora = null, string? Negative = null);

// Exploration Mode (the cold-start "I don't know what I want yet" lever): diverge one prompt into N seed-
// varied generations, then converge by refining the pick. Pure, GPU-free helpers (the seed plan + the
// similarity ladder) — the submit path reuses the existing single-flight gen queue, so this rides only
// CONFIRMED primitives (seed + img2img strength), nothing model-gated.
public static class Explore
{
    // The img2img "how similar to the pick" ladder — denoise/creativity from barely-changed to reinvented.
    // (Refine step: re-run the chosen image at one of these strengths.)
    public static readonly double[] SimilarityLadder = { 0.2, 0.35, 0.5, 0.65, 0.8 };

    // N seeds for a diverge batch. A fixed base seed -> reproducible NEIGHBORS (base, base+1, …) so a whole
    // exploration can be replayed; base < 0 -> all random (-1 = let SwarmUI pick each), maximizing spread.
    public static IReadOnlyList<int> Seeds(int baseSeed, int count)
    {
        count = Math.Clamp(count, 1, 16);
        var seeds = new List<int>(count);
        for (int i = 0; i < count; i++) seeds.Add(baseSeed >= 0 ? baseSeed + i : -1);
        return seeds;
    }
}
