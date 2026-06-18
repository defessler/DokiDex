using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// Persona "character card" store — the GPTs analog, local + uncensored. File-based under <home>/personas/
// <name>.json, a direct clone of References/SavedSearches, reusing the unit-tested RecipeStore.SafeName guard
// for the file name. Graceful try/catch like the recipe / reference / saved-search stores. The card shape
// (PersonaCard) lives in ChatPrompt.cs (the pure prompt-assembly core that consumes it).
public static class Persona
{
    private static string Dir => Path.Combine(RepoPaths.Root, "personas");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<PersonaCard> List()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<PersonaCard>();
        var outp = new List<PersonaCard>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            try
            {
                var c = JsonSerializer.Deserialize<PersonaCard>(File.ReadAllText(f), JsonOpts);
                if (c is not null && !string.IsNullOrWhiteSpace(c.Name)) outp.Add(c);
            }
            catch { }
        return outp.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static PersonaCard? Load(string? name)
    {
        var n = RecipeStore.SafeName(name);
        if (n is null) return null;
        var p = Path.Combine(Dir, n + ".json");
        try { return File.Exists(p) ? JsonSerializer.Deserialize<PersonaCard>(File.ReadAllText(p), JsonOpts) : null; }
        catch { return null; }
    }

    public static bool Save(PersonaCard? card)
    {
        var n = RecipeStore.SafeName(card?.Name);
        if (n is null) return false;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, n + ".json"), JsonSerializer.Serialize(card! with { Name = n })); return true; }
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
