using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// One stackable style chip: a named bundle of + prompt fragment (add) and - negative fragment (neg).
public sealed record Chip(string Id, string Name, string Add, string Neg);

// A browser request to apply selected chips to a prompt.
public sealed record StyleRequest(string? Prompt, string? Negative, List<string>? Chips);

// Style chips — stackable aesthetic bundles applied at submit, distinct from recipe chips. The curated
// catalog lives in media-assets/style-chips.json (user-extensible); Apply is the pure compose step (append
// each selected chip's positive fragment to the prompt and its negative fragment to the negative prompt).
// Pure Apply -> unit-tested; the endpoint resolves ids against the catalog and calls it.
public static class StyleChips
{
    private static List<Chip>? _cache;
    private sealed record Catalog(List<Chip>? Chips);

    public static IReadOnlyList<Chip> All()
    {
        if (_cache is not null) return _cache;
        try
        {
            var p = Path.Combine(RepoPaths.Root, "media-assets", "style-chips.json");
            var c = File.Exists(p)
                ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(p), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
            _cache = c?.Chips ?? new();
        }
        catch { _cache = new(); }
        return _cache;
    }

    // Pure: append each chip's add-fragment to the prompt and neg-fragment to the negative prompt (order
    // preserved, blanks skipped). Returns the composed (prompt, negative).
    public static (string Prompt, string Negative) Apply(IEnumerable<Chip> chips, string? prompt, string? negative)
    {
        var p = (prompt ?? "").Trim();
        var n = (negative ?? "").Trim();
        foreach (var c in chips)
        {
            if (!string.IsNullOrWhiteSpace(c.Add)) p = p.Length == 0 ? c.Add.Trim() : $"{p}, {c.Add.Trim()}";
            if (!string.IsNullOrWhiteSpace(c.Neg)) n = n.Length == 0 ? c.Neg.Trim() : $"{n}, {c.Neg.Trim()}";
        }
        return (p, n);
    }

    // Resolve selected ids against the catalog (preserving the catalog's order) and apply.
    public static (string Prompt, string Negative) Compose(string? prompt, string? negative, IEnumerable<string>? ids)
    {
        var want = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var selected = All().Where(c => want.Contains(c.Id));
        return Apply(selected, prompt, negative);
    }
}
