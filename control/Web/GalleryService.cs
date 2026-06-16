using System.IO;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// The persistent library: the app-owned generation output folder (DokiService.GenDir) is the source of
// truth (survives restarts), so the gallery scans it rather than the in-memory job list. Each artifact may
// have a `<file>.json` sidecar carrying the prompt/kind (written on completion). All file access is scoped
// to that folder with a canonical path check (the design's "scope /api/media to the index + path-prefix").
public sealed class GalleryService
{
    private static readonly string[] MediaExt = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm", ".mp3", ".wav", ".flac" };

    private static string Root => DokiService.GenDir;

    public IEnumerable<object> List(string? query = null, string? kindFilter = null)
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
            if (!Match(prompt, kind, query, kindFilter)) continue;   // saved-search / typed filter
            var when = File.GetLastWriteTime(f);
            items.Add((when, new
            {
                name,
                kind,
                prompt,
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

    public bool Delete(string name)
    {
        var full = Resolve(name);
        if (full is null) return false;
        try { File.Delete(full); var sc = full + ".json"; if (File.Exists(sc)) File.Delete(sc); return true; }
        catch { return false; }
    }

    public static void WriteSidecar(string artifactPath, string id, string kind, string prompt)
    {
        try { File.WriteAllText(artifactPath + ".json", JsonSerializer.Serialize(new Sidecar(id, kind, prompt, DateTime.Now.ToString("o")))); }
        catch { }
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

    public sealed record Sidecar(string Id, string Kind, string Prompt, string CreatedAt);
}
