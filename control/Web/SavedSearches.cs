using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A named, re-applicable Library filter (free-text query + kind + curation view).
public sealed record SavedSearch(string? Name, string? Q, string? Kind, string? View);

// Saved searches: a stored query that re-evaluates over the LIVE gallery, so it auto-files past AND future
// generations (the gallery is already the index). File-based under <home>/searches/<name>.json, reusing the
// unit-tested RecipeStore.SafeName guard for the file name. Graceful, like the recipe / reference stores.
public static class SavedSearches
{
    private static string Dir => Path.Combine(RepoPaths.Root, "searches");

    public static IReadOnlyList<SavedSearch> List()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<SavedSearch>();
        var outp = new List<SavedSearch>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            try
            {
                var s = JsonSerializer.Deserialize<SavedSearch>(File.ReadAllText(f), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s is not null && !string.IsNullOrWhiteSpace(s.Name)) outp.Add(s);
            }
            catch { }
        return outp.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Save(SavedSearch? s)
    {
        var n = RecipeStore.SafeName(s?.Name);
        if (n is null) return false;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, n + ".json"), JsonSerializer.Serialize(s! with { Name = n })); return true; }
        catch { return false; }
    }

    public static bool Delete(string? name)
    {
        var n = RecipeStore.SafeName(name);
        if (n is null) return false;
        var p = Path.Combine(Dir, n + ".json");
        try { if (File.Exists(p)) File.Delete(p); return true; }
        catch { return false; }
    }
}
