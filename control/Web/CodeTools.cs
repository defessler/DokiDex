using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DokiDex.Web;

// Workspace-scoped coding tools for the chat agent loop — the Claude-Code-parity surface. They let a local coder
// model READ and SEARCH the files of a single WORKSPACE ROOT (default: the repo root) so it can reason over real
// code, not just the semantic-only code_search RAG. READ-ONLY this slice (read_file; grep/glob next); the mutating
// tools (edit/write/run) land behind a permission gate in a later slice. Because the CHAT MODEL supplies these
// paths, every one is validated to stay UNDER the workspace root (no traversal / absolute escape) — the same
// canonical-prefix discipline as GalleryService / ChatTools.IsGallerySafePath. Pure seams are unit-tested, no disk.
public static class CodeTools
{
    // ---- security gate ----

    // Resolve a workspace-RELATIVE path to an absolute path GUARANTEED to live under `root`, or null if it escapes
    // the workspace (an absolute / drive / UNC input, or any "../" that canonicalizes outside root). The single
    // security gate every read / search / edit tool routes through. Pure + total — unit-tested for escape vectors.
    internal static string? ResolveWorkspacePath(string root, string? rel)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(rel)) return null;
        if (Path.IsPathRooted(rel)) return null;   // reject absolute / drive-qualified / UNC inputs outright
        string baseRoot, full;
        try
        {
            baseRoot = Path.GetFullPath(root);
            full = Path.GetFullPath(Path.Combine(baseRoot, rel));
        }
        catch { return null; }   // malformed path chars, PathTooLong, etc. — never throw at a tool boundary

        // Canonical-prefix containment: the resolved path must sit strictly UNDER the root (root + separator), so a
        // normalized "../" that stays inside is allowed but anything that climbs out — or a sibling dir sharing the
        // root's name prefix — is rejected. Case-insensitive to match Windows' filesystem.
        var prefix = baseRoot.EndsWith(Path.DirectorySeparatorChar)
            ? baseRoot
            : baseRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ---- read_file ----

    public const int ReadDefaultLimit = 1000;   // lines per read_file window (local-ctx-friendly; pages via offset)
    public const int ReadMaxChars = 16000;      // hard char cap on the returned block so a huge file can't bloat the loop

    // The read_file tool schema, in the OpenAI tool shape (mirrors ChatTools' schemas).
    public static readonly object ReadFileSchema = new
    {
        type = "function",
        function = new
        {
            name = "read_file",
            description = "Read a text file from the project workspace by its repo-relative path, returning the "
                + "requested line window with 1-based line numbers. Use this to see the ACTUAL contents of a file "
                + "(source, config, docs) when you need the real code — NOT semantic search (that's code_search). "
                + "Large files are windowed; pass offset/limit to page through.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Repo-relative path, e.g. 'control/Web/Chat.cs'. Must stay inside the workspace." },
                    offset = new { type = "integer", description = "1-based first line to read (default 1)." },
                    limit = new { type = "integer", description = "How many lines to read (default 1000)." },
                },
                required = new[] { "path" },
            },
        },
    };

    // PURE: parse read_file args {path, offset?, limit?} from the tool-call JSON STRING. path is trimmed (""=missing);
    // offset is the 1-based first line (default 1, floored at 1); limit is the line count (default ReadDefaultLimit,
    // floored at 1). Tolerant of string-typed ints (some models emit "offset":"10"). Malformed/missing => defaults.
    // Never throws. Total + side-effect-free — the unit-test seam.
    public static (string path, int offset, int limit) ParseReadArgs(string? json)
    {
        var path = "";
        var offset = 1;
        var limit = ReadDefaultLimit;
        if (string.IsNullOrWhiteSpace(json)) return (path, offset, limit);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (path, offset, limit);
            if (root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                path = (p.GetString() ?? "").Trim();
            offset = ReadIntProp(root, "offset", offset);
            limit = ReadIntProp(root, "limit", limit);
        }
        catch { /* keep defaults */ }
        if (offset < 1) offset = 1;
        if (limit < 1) limit = 1;
        return (path, offset, limit);
    }

    // Tolerant int property: a JSON number OR a string-typed int ("10"); anything else (absent/malformed) => dflt.
    private static int ReadIntProp(JsonElement root, string name, int dflt)
    {
        if (!root.TryGetProperty(name, out var v)) return dflt;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return dflt;
    }

    // PURE: render a line-window of a file into the tool-result text — 1-based line numbers (tab-separated, the
    // cat -n shape coder models expect from Claude Code's Read), a header naming the file + shown range + total, and
    // a trailing note when the window is cut short by the line limit OR the maxChars cap (telling the model the exact
    // offset to resume at). allLines is the WHOLE file; offset/limit select the window. Total + side-effect-free so
    // the bound can't silently drift — the disk read lives in RunReadFile, which passes its lines in.
    public static string FormatFileWindow(string relPath, IReadOnlyList<string> allLines, int offset, int limit, int maxChars)
    {
        var total = allLines.Count;
        if (offset < 1) offset = 1;
        if (limit < 1) limit = 1;
        var start = offset - 1;   // 0-based
        if (start >= total)
            return total == 0
                ? $"{relPath} is empty (0 lines)."
                : $"{relPath} has {total} line(s); offset {offset} is past the end.";

        var end = Math.Min(start + limit, total);   // exclusive
        var sb = new StringBuilder();
        sb.Append(relPath).Append(" (lines ").Append(start + 1).Append('-').Append(end)
          .Append(" of ").Append(total).Append("):\n");

        var capped = false;
        var shownEnd = end;
        for (var i = start; i < end; i++)
        {
            var lineText = $"{i + 1}\t{allLines[i]}\n";
            if (sb.Length + lineText.Length > maxChars) { capped = true; shownEnd = i; break; }
            sb.Append(lineText);
        }

        if (capped)
            sb.Append("… (truncated at ~").Append(maxChars).Append(" chars; ").Append(total - shownEnd)
              .Append(" more line(s) — read again with offset=").Append(shownEnd + 1).Append(").");
        else if (end < total)
            sb.Append("… (").Append(total - end).Append(" more line(s) — read again with offset=")
              .Append(end + 1).Append(").");
        return sb.ToString();
    }

    // The thin disk touch for read_file: validate the path through the workspace gate, read the file, and hand its
    // lines to the pure FormatFileWindow. Every failure (escape, missing, IO) degrades to a clear tool-result text —
    // the agent loop must never crash on a tool. `root` is the workspace root (the caller passes the repo root).
    internal static string RunReadFile(string root, string? argumentsJson)
    {
        var (path, offset, limit) = ParseReadArgs(argumentsJson);
        if (string.IsNullOrWhiteSpace(path))
            return "read_file needs a `path` (repo-relative, e.g. 'control/Web/Chat.cs').";
        var full = ResolveWorkspacePath(root, path);
        if (full is null)
            return $"\"{path}\" is outside the workspace or not an allowed path — use a repo-relative path.";
        try
        {
            if (!File.Exists(full)) return $"No file at \"{path}\".";
            var lines = File.ReadAllLines(full);
            return FormatFileWindow(path, lines, offset, limit, ReadMaxChars);
        }
        catch (Exception ex) { return $"read_file failed for \"{path}\": {ex.Message}"; }
    }

    // ---- grep ----

    public const int GrepMaxMatches = 50;    // total hits returned (bounds the tool text across the loop's hops)
    public const int GrepMaxLineLen = 240;   // per-line char cap (a minified/long line can't bloat the result)
    public const int GrepMaxFiles = 4000;    // files scanned before stopping (repo-scale safety net)

    // Dirs never worth scanning (VCS, build output, deps, big binary trees) and binary file extensions to skip.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".run", "bin", "obj", "node_modules", "packages", "dist", "build", ".venv", "__pycache__", "media", "models" };
    private static readonly HashSet<string> BinaryExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svgz", ".pdf", ".zip", ".gz", ".7z", ".rar",
          ".exe", ".dll", ".pdb", ".gguf", ".safetensors", ".bin", ".onnx", ".mp4", ".mov", ".webm", ".mp3", ".wav",
          ".flac", ".ttf", ".otf", ".woff", ".woff2", ".so", ".dylib", ".pyc", ".class", ".lock" };

    // The grep tool schema (OpenAI tool shape). Sharp, disjoint description vs code_search (literal/regex vs semantic).
    public static readonly object GrepSchema = new
    {
        type = "function",
        function = new
        {
            name = "grep",
            description = "Search the project's source files for a regular-expression PATTERN — a literal/regex "
                + "text search to find exact symbols, strings, or call sites. (For fuzzy 'where is X implemented' "
                + "questions use code_search instead.) Returns matching `path:line: text`, capped. Optional `path` "
                + "scopes the search to a sub-directory; optional `glob` filters file names (e.g. '*.cs').",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = ".NET regular expression to search for (e.g. 'class\\s+\\w+Controller')." },
                    path = new { type = "string", description = "Repo-relative sub-directory to scope the search to (optional; default = whole workspace)." },
                    glob = new { type = "string", description = "File-name filter with * and ? wildcards (optional; e.g. '*.cs', 'Chat*.cs')." },
                },
                required = new[] { "pattern" },
            },
        },
    };

    // PURE: parse grep args {pattern, path?, glob?}. pattern trimmed (""=missing); path/glob trimmed => null when blank.
    public static (string pattern, string? path, string? glob) ParseGrepArgs(string? json)
    {
        var pattern = "";
        string? path = null;
        string? glob = null;
        if (string.IsNullOrWhiteSpace(json)) return (pattern, path, glob);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (pattern, path, glob);
            if (root.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String) pattern = (p.GetString() ?? "").Trim();
            if (root.TryGetProperty("path", out var pa) && pa.ValueKind == JsonValueKind.String) { var v = (pa.GetString() ?? "").Trim(); if (v.Length > 0) path = v; }
            if (root.TryGetProperty("glob", out var g) && g.ValueKind == JsonValueKind.String) { var v = (g.GetString() ?? "").Trim(); if (v.Length > 0) glob = v; }
        }
        catch { /* keep defaults */ }
        return (pattern, path, glob);
    }

    private static readonly Regex MatchAll = new(".*", RegexOptions.Compiled);

    // PURE: translate a simple file-name glob (* and ? wildcards) to an anchored, case-insensitive Regex; a blank
    // glob matches everything. Every other char is escaped, so the translation can never throw. Total.
    internal static Regex GlobToRegex(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) return MatchAll;
        var sb = new StringBuilder("^");
        foreach (var c in glob.Trim())
            sb.Append(c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) });
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // PURE: scan one file's lines for the regex, returning (1-based line, text) per match, each line clipped to
    // maxLineLen so a long/minified line can't bloat the result. Total + side-effect-free — the unit-test seam.
    public static IReadOnlyList<(int line, string text)> MatchLines(IReadOnlyList<string> lines, Regex rx, int maxLineLen)
    {
        var hits = new List<(int, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!rx.IsMatch(lines[i])) continue;
            var t = lines[i];
            if (t.Length > maxLineLen) t = t[..maxLineLen] + "…";
            hits.Add((i + 1, t.TrimEnd()));
        }
        return hits;
    }

    // PURE: render grep hits into the bounded tool-result text — a header (count + pattern), then "path:line: text"
    // per hit, and a trailing note when the cap truncated the result. Total + side-effect-free.
    public static string FormatGrepResults(string pattern, IReadOnlyList<(string file, int line, string text)> hits, bool truncated)
    {
        if (hits.Count == 0) return $"No matches for /{pattern}/.";
        var sb = new StringBuilder();
        sb.Append(hits.Count).Append(truncated ? "+" : "").Append(" match(es) for /").Append(pattern).Append("/:\n");
        foreach (var (file, line, text) in hits)
            sb.Append(file).Append(':').Append(line).Append(": ").Append(text).Append('\n');
        if (truncated) sb.Append("… (capped at ").Append(GrepMaxMatches).Append(" matches — narrow the pattern or scope with `path`/`glob`).");
        return sb.ToString().TrimEnd('\n');
    }

    // The thin disk walk for grep: compile the regex, resolve+scope the search dir under the workspace, walk text
    // files (skipping VCS/build/dep/binary trees), collect up to GrepMaxMatches hits via the pure MatchLines, and
    // format. Every failure (bad regex, escape, IO) degrades to a clear tool-result text — never throws.
    internal static string RunGrep(string root, string? argumentsJson)
    {
        var (pattern, path, glob) = ParseGrepArgs(argumentsJson);
        if (string.IsNullOrWhiteSpace(pattern)) return "grep needs a `pattern` (a .NET regular expression).";

        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.CultureInvariant); }
        catch (Exception ex) { return $"grep: invalid regular expression /{pattern}/ — {ex.Message}"; }

        var scope = root;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var resolved = ResolveWorkspacePath(root, path);
            if (resolved is null) return $"\"{path}\" is outside the workspace.";
            scope = resolved;
        }
        if (!Directory.Exists(scope)) return $"No directory at \"{path ?? "."}\".";

        var nameFilter = GlobToRegex(glob);
        var hits = new List<(string file, int line, string text)>();
        var truncated = false;
        try
        {
            foreach (var file in WalkTextFiles(scope))
            {
                if (!nameFilter.IsMatch(Path.GetFileName(file))) continue;
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                foreach (var (line, text) in MatchLines(lines, rx, GrepMaxLineLen))
                {
                    hits.Add((rel, line, text));
                    if (hits.Count >= GrepMaxMatches) { truncated = true; break; }
                }
                if (truncated) break;
            }
        }
        catch (Exception ex) { return $"grep failed: {ex.Message}"; }

        return FormatGrepResults(pattern, hits, truncated);
    }

    // Recursive file walk that skips VCS/build/dependency/binary-heavy directories and binary file extensions,
    // bounded at GrepMaxFiles. Yields absolute file paths. Graceful on unreadable dirs.
    private static IEnumerable<string> WalkTextFiles(string scopeDir)
    {
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(scopeDir);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, subdirs;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            try { subdirs = Directory.GetDirectories(dir); } catch { subdirs = Array.Empty<string>(); }
            foreach (var f in files)
            {
                if (BinaryExts.Contains(Path.GetExtension(f))) continue;
                yield return f;
                if (++count >= GrepMaxFiles) yield break;
            }
            foreach (var d in subdirs)
                if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }
}
