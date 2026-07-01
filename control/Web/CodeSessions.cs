using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DokiDex.Web;

// Session persistence (1.4): `doki code` sessions used to die with the process — this makes them survive it, the
// way Claude Code does: stored CENTRALLY, OUTSIDE any repo (%USERPROFILE%\.doki\sessions\<workspace-hash>\
// <timestamp>.json — never in the worktree, so there's nothing to gitignore and no per-project churn), keyed by a
// short stable hash of the workspace root so distinct projects never collide. One file per SESSION (not per turn):
// Program.cs generates a session id once at startup and calls Save() after every completed turn, which OVERWRITES
// that one file — `--continue` loads the newest file for this workspace and keeps going; `/resume` lists and
// re-loads by index; `/export` renders the transcript as markdown.
//
// THE JsonElement WRINKLE (spec'd exactly, F3-R3): `working` holds anonymous C# objects (role/content/tool_calls/
// etc — see Chat.AppendToolRound, CodeOrientation.Build). Save just serializes the whole list as-is. On Load, the
// "messages" array is parsed back as JsonElement entries (via Clone(), so they outlive the JsonDocument they came
// from) and used DIRECTLY as `working` entries, cast to object — NO re-shaping into new anonymous objects.
// LocalLlm.Body/ChatToolsAsync re-serializes `working` on every request regardless of whether an entry is a plain
// anonymous object or a JsonElement (System.Text.Json's `object`-typed serialization picks the built-in JsonElement
// converter by runtime type, which just re-emits the original parsed JSON via WriteTo — same bytes, same property
// order), so a loaded session composes back into a request transparently. CodeContext.PropStr/IsSystemMessage/
// ToolCallNames (CodeAgent.cs) were upgraded alongside this to read EITHER shape too, so a resumed session's
// /clear, /compact, and /context (and this file's own ExportMarkdown) all still see real roles/content instead of
// silently misreading every reloaded message as blank, non-system history.
internal static class CodeSessions
{
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    // The real on-disk root. Every disk-touching method below takes an optional `sessionsRoot` override (mirrors
    // Updater.FindStagedUpdateIn's test seam) so tests point at a scratch temp dir instead of the user's real
    // profile; Program.cs always calls the overload with no override, which resolves to this.
    internal static string RealSessionsRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".doki", "sessions");

    // PURE: the first 12 lowercase hex chars of SHA-256 over the canonicalized workspace root — GetFullPath
    // (resolves "." / ".." / relative segments), trailing-separator-trimmed, lowercased (Windows paths are
    // case-insensitive) so trivial spelling differences of the SAME workspace still hash identically, while
    // distinct workspaces (almost certainly) don't collide. Total: GetFullPath's own exceptions (bad path chars,
    // PathTooLong) propagate — callers only ever pass an already-validated `root` (Program.cs's Directory.Exists
    // guard, or a test's own literal path).
    internal static string Hash(string root)
    {
        var full = Path.GetFullPath(root);
        var canon = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canon));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    // PURE (given the override): where this workspace's session files live.
    internal static string SessionDir(string root, string? sessionsRoot = null)
        => Path.Combine(sessionsRoot ?? RealSessionsRoot(), Hash(root));

    // A fresh session id — the sortable timestamp that doubles as both the filename stem and the "session id"
    // Program.cs keeps for the lifetime of the process (generated ONCE at session start; every Save() call for
    // that session reuses it, so the file is created at the first save and simply overwritten thereafter).
    internal static string NewSessionId() => DateTime.Now.ToString(TimestampFormat);

    // Persist `working` to THIS session's file (one file per session — `sessionId` is fixed for the process, so
    // repeated calls overwrite the same file rather than piling up one per turn). Atomic-ish: serialize to
    // "<id>.json.tmp" then File.Move(overwrite: true) it into place, so a crash mid-write leaves the PREVIOUS save
    // intact rather than a half-written file. Never throws: failures return false so the caller can print a single
    // dim note (Program.cs) instead of spamming one per turn.
    internal static bool Save(string root, string sessionId, IReadOnlyList<object> working, string? model, string? sessionsRoot = null)
    {
        try
        {
            var dir = SessionDir(root, sessionsRoot);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, sessionId + ".json");
            var tmp = file + ".tmp";
            var doc = new
            {
                workspace = Path.GetFullPath(root),
                model = model ?? "",
                saved = DateTime.UtcNow.ToString("o"),
                messages = working,
            };
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc));
            File.Move(tmp, file, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    // A session reloaded off disk: `Working` holds JsonElement entries (the wrinkle above), ready to use directly
    // as `working` — Program.cs assigns it wholesale (--continue / /resume), never rebuilding it turn by turn.
    internal sealed record LoadedSession(string Id, string Path, string? Workspace, string? Model, string? Saved, List<object> Working);

    // Parse one session file. Never throws — a missing/corrupt/foreign-shaped file degrades to null so a bad file
    // can't crash --continue or /resume; the caller reports "could not be read" rather than propagating.
    internal static LoadedSession? Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var working = new List<object>();
            if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                foreach (var el in msgs.EnumerateArray())
                    working.Add(el.Clone());   // Clone(): outlives `doc`, which is disposed when this method returns

            return new LoadedSession(System.IO.Path.GetFileNameWithoutExtension(path), path, Str("workspace"), Str("model"), Str("saved"), working);
        }
        catch { return null; }
    }

    // One row of a `/resume` listing: enough to show the user which session is which WITHOUT fully materializing
    // every session's `working` (List() only ever needs this summary, never the messages themselves).
    internal sealed record SessionSummary(string Id, string Path, int MessageCount, string FirstUserSnippet);

    internal const int SnippetMaxChars = 60;

    // PURE: given a saved session file's "messages" JSON array (NOT yet the constructed `working` list — this
    // reads the raw JsonElement directly, so List() never pays for a full Load() per file just to summarize it),
    // return (total message count, the first user turn's content clipped to `maxChars` with a trailing "…" when
    // cut). Snippet is "" when there is no user turn yet (e.g., a session saved right after /init before the user
    // has said anything) or `messages` isn't an array at all.
    internal static (int count, string snippet) SummarizeMessages(JsonElement messages, int maxChars = SnippetMaxChars)
    {
        if (messages.ValueKind != JsonValueKind.Array) return (0, "");
        var count = messages.GetArrayLength();
        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            if (!m.TryGetProperty("role", out var r) || r.ValueKind != JsonValueKind.String || r.GetString() != "user") continue;
            if (!m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) continue;
            var text = (c.GetString() ?? "").Trim();
            if (text.Length == 0) continue;
            return (count, text.Length > maxChars ? text[..maxChars] + "…" : text);
        }
        return (count, "");
    }

    // List this workspace's saved sessions, NEWEST FIRST (the sortable timestamp id sorts correctly as a plain
    // ordinal string). An unreadable/corrupt individual file is skipped rather than failing the whole listing; a
    // missing session dir (nothing saved yet) yields an empty list, not an error.
    internal static IReadOnlyList<SessionSummary> List(string root, string? sessionsRoot = null)
    {
        var dir = SessionDir(root, sessionsRoot);
        var result = new List<SessionSummary>();
        string[] files;
        try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>(); }
        catch { return result; }

        foreach (var f in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var messages = doc.RootElement.TryGetProperty("messages", out var m) ? m : default;
                var (count, snippet) = SummarizeMessages(messages);
                result.Add(new SessionSummary(Path.GetFileNameWithoutExtension(f), f, count, snippet));
            }
            catch { /* skip an unreadable/corrupt session file rather than fail the whole listing */ }
        }
        result.Sort((a, b) => string.CompareOrdinal(b.Id, a.Id));
        return result;
    }

    // The most recent session for this workspace (--continue), or null when none exist yet.
    internal static LoadedSession? LoadLatest(string root, string? sessionsRoot = null)
    {
        var list = List(root, sessionsRoot);
        return list.Count == 0 ? null : Load(list[0].Path);
    }

    // ---- /export: readable markdown (pure — no disk; Program.cs does the actual file write) ----

    // PURE: render `working` as role-headed markdown sections; a role:"tool" result is fenced (```` fences, not
    // triple-backtick, so tool output that itself contains a ``` fence — e.g. a Bash command that printed markdown
    // — can't break out of the block). Handles BOTH `working` shapes transparently via CodeContext's dual-shape
    // PropStr/ToolCallNames (CodeAgent.cs) — a session resumed via --continue/`/resume` (JsonElement entries)
    // exports identically to a live one (anonymous objects). System/orientation messages are included too (role-
    // headed, like everything else) — the plan calls for "role-headed sections", and this is meant as a faithful,
    // readable dump of the whole transcript, not a chat-only export.
    internal static string ExportMarkdown(IReadOnlyList<object> working)
    {
        var sb = new StringBuilder();
        sb.Append("# doki code session\n");
        foreach (var msg in working)
        {
            var role = CodeContext.PropStr(msg, "role") ?? "unknown";
            var content = CodeContext.PropStr(msg, "content");

            if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                var name = CodeContext.PropStr(msg, "name") ?? "tool";
                sb.Append("\n## tool: ").Append(name).Append('\n');
                sb.Append("````\n").Append((content ?? "").TrimEnd()).Append("\n````\n");
                continue;
            }

            sb.Append("\n## ").Append(Cap(role)).Append('\n');
            if (content is null)
            {
                var names = CodeContext.ToolCallNames(msg);
                sb.Append(names.Count > 0 ? "_called " + string.Join(", ", names) + "_\n" : "_(no content)_\n");
                continue;
            }
            sb.Append(content.TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
