using System.IO;
using System.Linq;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A save/load request for a named recipe (an ordered, reusable pipeline of gen steps, stored as CSV — the
// same column schema the batch runner uses).
public sealed record RecipeDto(string? Name, string? Csv);

// Saved recipes: the persistent, reusable slice of "node-flow" (the backlog's "start linear before full
// graph"). A recipe is a named CSV of gen steps under <home>/recipes/<name>.csv — distinct from one-shot CSV
// batch in that it's saved + reloadable + runnable later. File-based + graceful, like the gallery / LoRA list.
// The fragile part — a safe file name from user input — is the pure, unit-tested SafeName.
public static class RecipeStore
{
    private static string Dir => Path.Combine(RepoPaths.Root, "recipes");

    // Sanitize a recipe name to a single safe file stem: letters/digits/space/-/_ only, no separators or "..".
    // Returns null if the name is empty or contains anything else (no traversal, no subdirs).
    public static string? SafeName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64) return null;
        if (name.Contains("..")) return null;
        return name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_') ? name : null;
    }

    public static IEnumerable<string> List()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(Dir, "*.csv")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool Save(string? name, string? csv)
    {
        var n = SafeName(name);
        if (n is null) return false;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, n + ".csv"), csv ?? ""); return true; }
        catch { return false; }
    }

    public static string? Load(string? name)
    {
        var n = SafeName(name);
        if (n is null) return null;
        var p = Path.Combine(Dir, n + ".csv");
        try { return File.Exists(p) ? File.ReadAllText(p) : null; }
        catch { return null; }
    }

    public static bool Delete(string? name)
    {
        var n = SafeName(name);
        if (n is null) return false;
        var p = Path.Combine(Dir, n + ".csv");
        try { if (File.Exists(p)) File.Delete(p); return true; }
        catch { return false; }
    }
}
