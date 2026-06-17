using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// One effect preset: a named one-click stylistic transform (a prompt + img2img strength).
public sealed record EffectPreset(string Id, string Name, string Prompt, double Strength);

// The one-click effect-preset gallery: a curated, user-extensible catalog (media-assets/effect-presets.json)
// of stylistic transforms applied to a finished card as img2img — "to anime / to night / watercolor / 3d".
// Build is the pure compose (preset + the card image -> an img2img GenRequest); the endpoint resolves the
// preset by id and submits through the existing queue (reuses the per-card refine mechanism).
public static class EffectPresets
{
    private static List<EffectPreset>? _cache;
    private sealed record Catalog(List<EffectPreset>? Presets);

    public static IReadOnlyList<EffectPreset> All()
    {
        if (_cache is not null) return _cache;
        try
        {
            var p = Path.Combine(RepoPaths.Root, "media-assets", "effect-presets.json");
            var c = File.Exists(p)
                ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(p), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
            _cache = c?.Presets ?? new();
        }
        catch { _cache = new(); }
        return _cache;
    }

    public static EffectPreset? Find(string? id) => All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    // Pure: a preset + the card's image -> an img2img GenRequest (the preset's prompt at its strength). The card
    // image is the structure source; strength controls how far the restyle goes.
    public static GenRequest Build(EffectPreset preset, string initImagePath)
        => new(preset.Prompt, "image", InitImage: initImagePath, Strength: preset.Strength);
}
