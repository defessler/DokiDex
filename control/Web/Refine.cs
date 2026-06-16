using DokiDex.Control.Services;

namespace DokiDex.Web;

// A browser request to refine a finished generation (a post action on its card).
public sealed record RefineRequest(string? Action);

// Per-card downstream actions: take a finished image and re-run it as img2img with one refine flag —
// "refine-from-result", chaining the output back as input. Uses ONLY confirmed mechanisms (init image +
// the existing -Face / -Refine / -Upscale recipe knobs), so it's fully runnable, not model-gated.
//   • face    -> light img2img + the <segment:face> refine pass (the ADetailer move)
//   • hires   -> hi-res-fix (refiner control% 0.35 + tiling) at moderate creativity
//   • upscale -> pure 4x upscale, creativity 0 (keep the image, just enlarge)
public static class Refine
{
    public static GenRequest? Build(string prompt, string initImagePath, string? action) => (action ?? "").Trim().ToLowerInvariant() switch
    {
        "face"    => new GenRequest(prompt, "image", InitImage: initImagePath, Face: true,    Strength: 0.35),
        "hires"   => new GenRequest(prompt, "image", InitImage: initImagePath, Refine: true,  Strength: 0.40),
        "upscale" => new GenRequest(prompt, "image", InitImage: initImagePath, Upscale: true, Strength: 0.0),
        _         => null,
    };

    public static readonly string[] Actions = { "face", "hires", "upscale" };
}
