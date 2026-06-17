using System.IO;
using System.Linq;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A save request for a named @-reference (a reusable prompt snippet).
public sealed record ReferenceDto(string? Name, string? Text);

// A named @-reference with its snippet text (the "cast/Elements" view, e.g. for the pitch-deck export).
public sealed record ReferenceEntry(string Name, string Text);

// The @-reference shelf: named, reusable prompt snippets under <home>/references/<name>.txt. `@name` in any
// prompt expands to the snippet (doki-gen's Expand-References, single source of truth) — reusable character/
// style building blocks for consistency. The web manages the files; the recipe expands them. File-based +
// graceful; the safe-name guard is the shared, unit-tested RecipeStore.SafeName.
public static class References
{
    private static string Dir => Path.Combine(RepoPaths.Root, "references");

    public static IEnumerable<object> List()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<object>();
        return Directory.EnumerateFiles(Dir, "*.txt")
            .Select(f => new { name = Path.GetFileNameWithoutExtension(f), text = SafeRead(f) })
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Typed projection (name + snippet) — the named character/style "Elements" of the project.
    public static IReadOnlyList<ReferenceEntry> Entries()
        => !Directory.Exists(Dir) ? Array.Empty<ReferenceEntry>()
         : Directory.EnumerateFiles(Dir, "*.txt")
            .Select(f => new ReferenceEntry(Path.GetFileNameWithoutExtension(f), SafeRead(f)))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool Save(string? name, string? text)
    {
        var n = RecipeStore.SafeName(name);
        if (n is null) return false;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, n + ".txt"), (text ?? "").Trim()); return true; }
        catch { return false; }
    }

    public static bool Delete(string? name)
    {
        var n = RecipeStore.SafeName(name);
        if (n is null) return false;
        var p = Path.Combine(Dir, n + ".txt");
        try { if (File.Exists(p)) File.Delete(p); return true; }
        catch { return false; }
    }

    private static string SafeRead(string f) { try { return File.ReadAllText(f).Trim(); } catch { return ""; } }
}
