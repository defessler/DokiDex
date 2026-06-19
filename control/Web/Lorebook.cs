using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// One lorebook entry ("World Info" rule): when ANY of its Keys appears as a whole word in the recent transcript,
// its Content is injected into the prompt. Keys = a comma-separated list (e.g. "dragon, wyrm, drake"). Enabled
// gates it off without deleting it. Mirrors SillyTavern's keyword World Info, minus the regex/selective/recursion
// knobs (deliberately out of P3 scope).
public sealed record LoreEntry(string? Keys, string? Content, bool Enabled = true);

// A named collection of lore entries, stored as lorebooks/<name>.json by the Lorebook store below.
public sealed record LoreBook(string? Name, IReadOnlyList<LoreEntry> Entries);

// Lorebook-lite / "World Info": keyword-triggered context injection.
//   • Activate — the PURE, unit-tested heart: whole-word, case-insensitive key match (reusing the
//     \b{Regex.Escape(key)}\b technique proven in Tts.ApplyLexicon), enabled-only, de-duplicated, capped to
//     maxEntries + a cumulative maxChars budget. Total + side-effect-free (no GPU, no disk).
//   • the file store (List/Load/Save/Delete) — a direct clone of Persona/RecipeStore, guarded by the unit-tested
//     RecipeStore.SafeName, graceful try/catch, JSON under <home>/lorebooks/<name>.json.
public static class Lorebook
{
    private static string Dir => Path.Combine(RepoPaths.Root, "lorebooks");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Return the entries whose ANY key appears as a WHOLE WORD (case-insensitive) in scanText: enabled-only,
    // de-duplicated (by reference + by Keys/Content), capped to maxEntries and a cumulative maxChars budget.
    // Order is preserved from `entries` (most-specific-wins ordering is the caller's responsibility).
    public static IReadOnlyList<LoreEntry> Activate(
        IReadOnlyList<LoreEntry> entries, string scanText, int maxEntries, int maxChars)
    {
        var outp = new List<LoreEntry>();
        if (entries is null || entries.Count == 0 || string.IsNullOrWhiteSpace(scanText)) return outp;

        var seen = new HashSet<(string?, string?)>();
        var usedChars = 0;

        foreach (var e in entries)
        {
            if (e is null || !e.Enabled) continue;
            if (outp.Count >= maxEntries) break;

            var content = e.Content ?? "";

            var keys = (e.Keys ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keys.Length == 0) continue;

            var matched = keys.Any(k => Regex.IsMatch(scanText, $@"\b{Regex.Escape(k)}\b", RegexOptions.IgnoreCase));
            if (!matched) continue;

            // de-dup identical entries (by key set + content), AFTER the enabled + keyword-match gates so de-dup
            // applies only among ACTIVATED entries — a non-matching or disabled entry never consumes a slot.
            // (reference-dups collapse here too.)
            if (!seen.Add((e.Keys, e.Content))) continue;

            // cumulative char budget: skip an entry that would overflow (keep scanning smaller later ones).
            if (usedChars + content.Length > maxChars) continue;
            usedChars += content.Length;

            outp.Add(e);
        }

        return outp;
    }

    public static IEnumerable<string> List()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(Dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static LoreBook? Load(string? name)
    {
        var n = RecipeStore.SafeName(name);
        if (n is null) return null;
        var p = Path.Combine(Dir, n + ".json");
        try { return File.Exists(p) ? JsonSerializer.Deserialize<LoreBook>(File.ReadAllText(p), JsonOpts) : null; }
        catch { return null; }
    }

    public static bool Save(LoreBook? book)
    {
        var n = RecipeStore.SafeName(book?.Name);
        if (n is null) return false;
        try
        {
            Directory.CreateDirectory(Dir);
            var normalized = book! with { Name = n, Entries = book.Entries ?? Array.Empty<LoreEntry>() };
            File.WriteAllText(Path.Combine(Dir, n + ".json"), JsonSerializer.Serialize(normalized));
            return true;
        }
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
