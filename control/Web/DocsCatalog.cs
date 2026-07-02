using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DokiDex.Web;

// One doc entry exposed by GET /api/docs (the list). Never carries a path -- the SPA only ever knows the id.
public sealed record DocEntry(string Id, string Title, string Group);

// The id -> file mapping the server assigns for one whitelisted doc. RelPath is root-relative, forward-slash,
// already validated by IsAllowedRelPath (BuildMap is the only place a DocMapping gets constructed). TitleOverride
// is set only for README.md ("Overview" -- its own H1 just says "DokiDex", redundant next to the app chrome).
public sealed record DocMapping(string RelPath, string? TitleOverride = null);

// GET /api/docs (list) + GET /api/docs/{id} (content) backing store: the in-app Help view's ONLY doorway into
// repo markdown. This is a WHITELIST, never a directory scan of arbitrary paths -- the four "guides" docs are
// hardcoded; docs/wiki/*.md is the one directory that gets enumerated, and every discovered file (core or wiki)
// is re-validated through the SAME IsAllowedRelPath gate before it's allowed into the id->file map. The map's ids
// are server-assigned slugs (WikiSlug / the hardcoded CoreDocs ids) -- a client can only ever look up an id that
// already exists in this map; there is no code path that turns client input into a filesystem path (F3 security).
public static class DocsCatalog
{
    // Full-content read cap for GET /api/docs/{id} (~200KB per the leaf spec) -- generous for any doc in this
    // corpus, but a hard ceiling so a future giant file can't balloon a response.
    public const int MaxMarkdownChars = 200_000;

    // Cheap read cap used only to sniff a doc's title for the LIST endpoint -- every doc in the corpus puts its
    // H1 on/near line 1, so a small prefix read avoids paging in a whole (possibly large) file just for a title.
    private const int TitleScanChars = 4_096;

    // The WHITELIST for the four non-wiki docs (id, root-relative path, optional display-title override). Never
    // derived from a scan -- this is the literal, hand-picked set the leaf spec calls out.
    public static readonly IReadOnlyList<(string Id, string RelPath, string? TitleOverride)> CoreDocs = new (string, string, string?)[]
    {
        ("readme",       "README.md",            "Overview"),
        ("quickstart",   "docs/quickstart.md",   null),
        ("tutorial",     "docs/tutorial.md",     null),
        ("capabilities", "docs/CAPABILITIES.md", null),
    };

    // PURE: turn a wiki filename ("1-the-big-idea.md") into a stable id slug ("wiki-1-the-big-idea"). Lowercases
    // the stem and replaces every non-alphanumeric run with a single '-', so any future wiki filename still
    // produces a clean, collision-safe id without needing a hand-maintained table.
    public static string WikiSlug(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "").ToLowerInvariant();
        var chars = stem.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--")) collapsed = collapsed.Replace("--", "-");
        return "wiki-" + collapsed.Trim('-');
    }

    // PURE: numbered wiki files ("1-the-big-idea.md" .. "12-benchmarks.md") sort in NARRATIVE order (1,2,…,12),
    // not filename-string order (which would put "10-…" before "2-…"); Home.md (no leading digits) sorts first,
    // as the wiki's own landing page. Any filename without a leading number sorts alongside Home at the front.
    public static int WikiOrderKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        var digits = new string(stem.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var n) ? n : -1;
    }

    // PURE: the whitelist gate every relPath must pass before it can enter the id->file map -- (b) the doc must
    // live directly under docs/wiki/ or docs/, or be exactly README.md, and (c) it must be a .md file. A literal
    // ".." anywhere in the path is rejected outright (belt-and-suspenders alongside ResolveSafe's containment
    // check, which re-validates against the actual resolved filesystem path).
    public static bool IsAllowedRelPath(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        var norm = relPath.Replace('\\', '/').Trim();
        if (norm.Contains("..", StringComparison.Ordinal)) return false;
        if (!norm.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(norm, "README.md", StringComparison.Ordinal)) return true;
        if (norm.StartsWith("docs/wiki/", StringComparison.Ordinal) && norm.Length > "docs/wiki/".Length) return true;
        if (norm.StartsWith("docs/", StringComparison.Ordinal) && norm.Length > "docs/".Length) return true;
        return false;
    }

    // PURE: build the id -> DocMapping table from a set of server-discovered (id, relPath, titleOverride) tuples.
    // ANY tuple whose relPath fails IsAllowedRelPath is silently dropped -- this is the ONE place the map gets
    // built, so "no client-controlled paths can ever land in the map" reduces to "this filter is correct", which
    // is exactly what DocsCatalogTests exercises directly (no disk, no client involved).
    public static IReadOnlyDictionary<string, DocMapping> BuildMap(
        IEnumerable<(string Id, string RelPath, string? TitleOverride)> discovered)
    {
        var map = new Dictionary<string, DocMapping>(StringComparer.Ordinal);
        foreach (var (id, relPath, titleOverride) in discovered)
        {
            if (string.IsNullOrWhiteSpace(id) || !IsAllowedRelPath(relPath)) continue;
            map[id] = new DocMapping(relPath.Replace('\\', '/').Trim(), titleOverride);
        }
        return map;
    }

    // Build the full id -> DocMapping table: the 4 hardcoded CoreDocs + docs/wiki/*.md enumerated fresh off disk
    // (ordered Home-first then narratively by WikiOrderKey). Every entry -- core or wiki -- still passes through
    // BuildMap's whitelist filter; the wiki scan is inherently safe (Directory.EnumerateFiles only yields real
    // files that already live inside docs/wiki/), so this filter is defense-in-depth, not the primary guarantee.
    public static IReadOnlyDictionary<string, DocMapping> DiscoverAll(string root)
    {
        var discovered = new List<(string, string, string?)>(CoreDocs);
        var wikiDir = Path.Combine(root, "docs", "wiki");
        if (Directory.Exists(wikiDir))
        {
            var files = Directory.EnumerateFiles(wikiDir, "*.md", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(WikiOrderKey)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
            foreach (var name in files)
                discovered.Add((WikiSlug(name), "docs/wiki/" + name, null));
        }
        return BuildMap(discovered);
    }

    // PURE: resolve a (root, relPath) pair to an absolute path, but ONLY if relPath is whitelisted AND the
    // resolved path actually lands inside root (the containment re-check: relPath already can't contain "..",
    // but this catches anything a future whitelist tweak might miss). Returns null on any failure.
    public static string? ResolveSafe(string root, string relPath)
    {
        if (!IsAllowedRelPath(relPath)) return null;
        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relPath));
        var rootWithSep = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    // PURE: the display group -- "wiki" for anything under docs/wiki/, "guides" for the four core docs.
    public static string GroupFor(string relPath)
        => relPath.Replace('\\', '/').StartsWith("docs/wiki/", StringComparison.Ordinal) ? "wiki" : "guides";

    // PURE: the filename-derived fallback title (e.g. "docs/wiki/1-the-big-idea.md" -> "1-the-big-idea").
    public static string FallbackTitle(string relPath) => Path.GetFileNameWithoutExtension(relPath);

    // PURE: scan every line for the first ATX H1 ("# heading"), trimmed; "##"+ headings never match. Falls back
    // to `fallback` when the markdown is empty or no H1 is found (e.g. a doc that opens with something else).
    public static string ExtractTitle(string? markdown, string fallback)
    {
        if (string.IsNullOrEmpty(markdown)) return fallback;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var t = line[2..].Trim();
                if (t.Length > 0) return t;
            }
        }
        return fallback;
    }

    // PURE: the title to show for one mapping -- an explicit TitleOverride (README's "Overview") always wins;
    // otherwise the first H1 in `markdown`, else the filename fallback.
    public static string ResolveTitle(DocMapping mapping, string? markdown)
        => mapping.TitleOverride ?? ExtractTitle(markdown, FallbackTitle(mapping.RelPath));

    // Read up to `maxChars` characters of a file (a blocking read, so short files still return their full
    // content in one call). Used both for the cheap title sniff (TitleScanChars) and the full-content read
    // (MaxMarkdownChars) -- the only difference is the cap.
    public static string ReadCapped(string fullPath, int maxChars)
    {
        using var sr = new StreamReader(fullPath);
        var buf = new char[maxChars];
        var read = sr.ReadBlock(buf, 0, buf.Length);
        return new string(buf, 0, read);
    }

    // Build one DocEntry (id/title/group) for the LIST endpoint -- reads only a small title-sniff prefix, never
    // the full doc. Degrades to the filename fallback if the file is missing/unreadable (never throws).
    public static DocEntry ToListEntry(string root, string id, DocMapping mapping)
    {
        string? head = null;
        var full = ResolveSafe(root, mapping.RelPath);
        try { if (full is not null && File.Exists(full)) head = ReadCapped(full, TitleScanChars); }
        catch { /* degrade to the filename fallback below */ }
        return new DocEntry(id, ResolveTitle(mapping, head), GroupFor(mapping.RelPath));
    }
}
