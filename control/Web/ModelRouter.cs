using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// An installed image checkpoint the router may choose (File = the SwarmUI model name, basename w/ extension).
public sealed record RoutableModel(string Id, string File, string Name, bool IsDefault);

// A request to generate one prompt across every installed image base (the compare grid).
public sealed record CompareRequest(string? Prompt);

public enum ImgClass { Versatile, Photo, Illustration, Text }

// Auto model router: a cheap, prompt-aware checkpoint picker (Freepik "Auto") — read the prompt, pick the
// installed base that suits it (photoreal vs illustration vs text-strong) BEFORE generating; never hides the
// explicit picker. Pure + total -> unit-tested. With one base it collapses to that base; it matters once ≥2
// are installed. Conservative: when the wanted class isn't available it returns the user's default base.
public static class ModelRouter
{
    // What does the prompt seem to want? Keyword heuristic (no model, no LLM); Versatile when unsure.
    public static ImgClass WantedClass(string? prompt)
    {
        var p = (prompt ?? "").ToLowerInvariant();
        if (HasAny(p, "logo", "poster", "typography", "lettering", "signage", "billboard")) return ImgClass.Text;
        if (HasAny(p, "anime", "manga", "illustration", "cartoon", "cel ", "cel-", "comic", "chibi", "furry", "drawing", "line art", "lineart", "painterly", "watercolor")) return ImgClass.Illustration;
        if (HasAny(p, "photo", "photograph", "photoreal", "realistic", "portrait", "dslr", "35mm", "85mm", "bokeh", "cinematic still", "hyperrealistic")) return ImgClass.Photo;
        return ImgClass.Versatile;
    }

    // Classify a checkpoint by its name (the catalog has no class tag); Versatile by default.
    public static ImgClass ClassifyModel(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (HasAny(n, "ideogram", "text", "glyph")) return ImgClass.Text;
        if (HasAny(n, "anime", "illustr", "pony", "toon", "manga", "chroma", "art")) return ImgClass.Illustration;
        if (HasAny(n, "real", "photo", "flux", "juggernaut", "epic", "dream")) return ImgClass.Photo;
        return ImgClass.Versatile;
    }

    // Pick the best installed image model for a prompt: an exact non-versatile class match wins; otherwise the
    // user's default (or the first). Null only when nothing is installed.
    public static RoutableModel? Pick(string? prompt, IReadOnlyList<RoutableModel> installed)
    {
        if (installed is null || installed.Count == 0) return null;
        var want = WantedClass(prompt);
        if (want != ImgClass.Versatile)
        {
            var match = installed.FirstOrDefault(m => ClassifyModel(m.Name) == want);
            if (match is not null) return match;
        }
        return installed.FirstOrDefault(m => m.IsDefault) ?? installed[0];
    }

    private static bool HasAny(string s, params string[] needles) => needles.Any(s.Contains);
}
