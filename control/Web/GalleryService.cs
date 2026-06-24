using System.IO;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A keyboard-triage decision posted from the Library grid: flip favorite and/or trash (null = leave as-is).
public sealed record RateRequest(bool? Favorite, bool? Trash);

// The persistent library: the app-owned generation output folder (DokiService.GenDir) is the source of
// truth (survives restarts), so the gallery scans it rather than the in-memory job list. Each artifact may
// have a `<file>.json` sidecar carrying the prompt/kind (written on completion). All file access is scoped
// to that folder with a canonical path check (the design's "scope /api/media to the index + path-prefix").
public sealed class GalleryService
{
    private static readonly string[] MediaExt = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm", ".mp3", ".wav", ".flac" };
    // Exposed for path-safety checks in ChatTools (P1-2 fix: validate edit_image source extensions).
    internal static readonly HashSet<string> MediaExtensions = new(MediaExt, StringComparer.OrdinalIgnoreCase);

    private static string Root => DokiService.GenDir;

    public IEnumerable<object> List(string? query = null, string? kindFilter = null, string? view = null)
    {
        var root = Root;
        if (!Directory.Exists(root)) return Array.Empty<object>();
        var items = new List<(DateTime when, object dto)>();
        foreach (var f in Directory.EnumerateFiles(root))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Array.IndexOf(MediaExt, ext) < 0) continue;
            var name = Path.GetFileName(f);
            var meta = ReadSidecar(f);
            var kind = meta?.Kind ?? KindFromExt(ext);
            var prompt = meta?.Prompt ?? "";
            var fav = meta?.Favorite ?? false;
            var trash = meta?.Trash ?? false;
            if (!Match(prompt, kind, query, kindFilter)) continue;   // saved-search / typed filter
            if (!PassesView(fav, trash, view)) continue;             // keyboard-triage curation view
            var when = File.GetLastWriteTime(f);
            items.Add((when, new
            {
                name,
                kind,
                prompt,
                favorite = fav,
                trash,
                parent = meta?.Parent,
                mediaUrl = $"/api/gallery/media/{Uri.EscapeDataString(name)}",
                date = when.ToString("o"),
            }));
        }
        return items.OrderByDescending(i => i.when).Select(i => i.dto);
    }

    // Pure library filter: a free-text query matches anywhere in the prompt (case-insensitive); a kind filter
    // must equal the item's kind. Blank/null filters match everything. Total -> unit-tested.
    public static bool Match(string prompt, string kind, string? query, string? kindFilter)
    {
        if (!string.IsNullOrWhiteSpace(kindFilter) && !string.Equals(kind, kindFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query) && (prompt ?? "").IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    // Pure curation view, for keyboard triage (number/letter keys flip favorite/trash, the chips filter):
    //   null/""/"all" -> everything   "active" -> not trashed (the default working set)
    //   "fav" -> favorites only       "trash" -> trashed only       "untriaged" -> neither flag set
    // Total -> unit-tested. Trash always loses to an explicit "fav"/"trash"/"untriaged" ask.
    public static bool PassesView(bool favorite, bool trash, string? view) => (view ?? "").Trim().ToLowerInvariant() switch
    {
        "fav" or "favorites" or "favorite" => favorite && !trash,
        "trash" or "trashed"               => trash,
        "untriaged" or "untagged"          => !favorite && !trash,
        "active" or "keep"                 => !trash,
        _                                  => true,   // all / unknown
    };

    // Read a sidecar by gallery name (scoped through Resolve), or null. Public for triage + lineage.
    public Sidecar? Read(string name) { var f = Resolve(name); return f is null ? null : ReadSidecar(f); }

    // Apply a triage decision: flip favorite/trash on an existing artifact, preserving every other sidecar
    // field. Null leaves a flag unchanged. Returns the merged sidecar, or null if the artifact is unknown.
    public Sidecar? Rate(string name, bool? favorite, bool? trash)
    {
        var f = Resolve(name);
        if (f is null) return null;
        var cur = ReadSidecar(f) ?? new Sidecar(name, KindFromExt(Path.GetExtension(f).ToLowerInvariant()), "", File.GetLastWriteTime(f).ToString("o"));
        var next = cur with { Favorite = favorite ?? cur.Favorite, Trash = trash ?? cur.Trash };
        try { File.WriteAllText(f + ".json", JsonSerializer.Serialize(next)); return next; } catch { return null; }
    }

    // Resolve a gallery file name to a full path, ONLY if it sits directly in Root (no traversal, no subdirs).
    public string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        var rootFull = Path.GetFullPath(Root);
        var full = Path.GetFullPath(Path.Combine(rootFull, name));
        if (!string.Equals(Path.GetDirectoryName(full), rootFull, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(full)) return null;
        if (Array.IndexOf(MediaExt, Path.GetExtension(full).ToLowerInvariant()) < 0) return null;
        return full;
    }

    // Read a gallery IMAGE as a data: URL (scoped through Resolve), for the multimodal LLM (Describe/Verify).
    // Null for unknown names or non-image kinds (we don't ship video/audio frames to a VLM here).
    public string? ImageDataUrl(string name)
    {
        var full = Resolve(name);
        if (full is null) return null;
        var mime = Mime(full);
        if (!mime.StartsWith("image/")) return null;
        try { return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}"; } catch { return null; }
    }

    public bool Delete(string name)
    {
        var full = Resolve(name);
        if (full is null) return false;
        try { File.Delete(full); var sc = full + ".json"; if (File.Exists(sc)) File.Delete(sc); return true; }
        catch { return false; }
    }

    public static void WriteSidecar(string artifactPath, string id, string kind, string prompt, string? parent = null)
    {
        try { File.WriteAllText(artifactPath + ".json", JsonSerializer.Serialize(new Sidecar(id, kind, prompt, DateTime.Now.ToString("o"), Parent: parent))); }
        catch { }
    }

    // Typed projection of the library for the variation-lineage forest (name + its parent link + label/kind).
    // Sorted newest-first for a stable, sensible tree order; no view/query filtering (lineage shows everything).
    public IReadOnlyList<LinItem> LineageItems()
    {
        var root = Root;
        var outp = new List<(DateTime when, LinItem it)>();
        if (!Directory.Exists(root)) return Array.Empty<LinItem>();
        foreach (var f in Directory.EnumerateFiles(root))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Array.IndexOf(MediaExt, ext) < 0) continue;
            var meta = ReadSidecar(f);
            outp.Add((File.GetLastWriteTime(f), new LinItem(Path.GetFileName(f), meta?.Parent, meta?.Prompt ?? "", meta?.Kind ?? KindFromExt(ext))));
        }
        return outp.OrderByDescending(x => x.when).Select(x => x.it).ToList();
    }

    private static Sidecar? ReadSidecar(string artifactPath)
    {
        try { var p = artifactPath + ".json"; return File.Exists(p) ? JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(p)) : null; }
        catch { return null; }
    }

    private static string KindFromExt(string ext) => ext switch
    {
        ".mp4" or ".webm" => "video",
        ".mp3" or ".wav" or ".flac" => "music",
        _ => "image",
    };

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".flac" => "audio/flac",
        _ => "application/octet-stream",
    };

    // Favorite/Trash = the keyboard-triage curation flags; Parent = the source artifact name this was derived
    // from (refine/effect/vary/explore), forming the variation-lineage forest. All default so every existing
    // 4-arg WriteSidecar call (and any sidecar written before these fields existed) still deserializes.
    public sealed record Sidecar(string Id, string Kind, string Prompt, string CreatedAt,
                                 bool Favorite = false, bool Trash = false, string? Parent = null);
}
