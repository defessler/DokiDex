using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// memory_recall sidecar — pulls the user's persistent long-term memory notes (serving/memory-mcp/memory_db.py,
// sqlite+FTS5) for [Memory] context-injection into the chat prompt, run one-shot through `uv run python
// memory_db.py ...` exactly like DocSearch/CodeSearch. memory_db.py is stdlib-only, so plain `uv run python` needs
// no extra packages. This is the chat surface's read path onto the SAME store the memory-mcp server exposes to the
// coding agent — not a 5th chat tool (recall is unconditional context-injection, like [World Info]/[Documents]).
//
// DEGRADE, never throw, never hang: a missing sidecar / uv / python, a non-zero exit, a timeout, or malformed
// output all yield an EMPTY note list — so ChatPrompt.Build injects NOTHING and chat proceeds byte-for-byte
// unchanged (the no-memory path). Same contract as DocSearch.RetrieveAsync (20s cap + KillTree).
//
// Pure seams (unit-tested, no process): BuildRecentArgs, BuildSearchArgs, ParseMemoryJson, ToMemoryNotes.
// One editable memory row for the admin surface (the /api/memory list + a future panel): the id is needed to
// edit/delete, unlike the injection path (MemoryNote) which only needs the fact text. Mirrors memory_db.py's row.
public sealed record MemoryRecord(int Id, string Content, string Tags);

// POST /api/memory body: the fact to remember + optional comma tags (id + timestamp are server-set by the store).
public sealed record MemorySaveRequest(string? Content, string? Tags);

public static class MemoryRecall
{
    // Recent notes pulled per turn (the newest-first memory set); ChatPrompt.MemoryBlock re-bounds to
    // MemoryMaxNotes / MemoryMaxChars, so this is just the fetch ceiling.
    private const int RetrieveK = 12;

    private static string Script => Path.Combine(RepoPaths.Root, "serving", "memory-mcp", "memory_db.py");

    // The default memory store DB (memory_db.py's DB_PATH when MEMORY_DB isn't overridden). Recall is gated on the
    // DB EXISTING so a chat with NO saved memories pays ZERO cost (no per-turn subprocess) — the store is created by
    // the SAVE paths (the memory-mcp server / seed.py / a future chat save-tool), never by this read path.
    private static string DbPath => Path.Combine(RepoPaths.Root, "serving", "memory-mcp", "memory.db");

    public static bool Installed => File.Exists(Script) && File.Exists(DbPath);

    // PURE: argv for `uv run python memory_db.py recent N` (the newest-first memory set). N clamped to [1,50].
    public static IReadOnlyList<string> BuildRecentArgs(int limit)
        => new[] { Script, "recent", Math.Clamp(limit, 1, 50).ToString() };

    // PURE: argv for `uv run python memory_db.py search Q N` (FTS5/LIKE match). N clamped to [1,50]. (Reserved for
    // a future turn-relevant recall mode; slice-1 recall uses BuildRecentArgs — inject the recent memory set.)
    public static IReadOnlyList<string> BuildSearchArgs(string query, int limit)
        => new[] { Script, "search", query ?? "", Math.Clamp(limit, 1, 50).ToString() };

    // Retrieve the recent memory notes for one turn, mapped to MemoryNotes for ChatPrompt injection. ANY failure
    // (sidecar absent, uv/python missing, timeout, non-zero exit, malformed output) degrades to an EMPTY list — so
    // the [Memory] block is omitted and chat is byte-for-byte the no-memory path. Never throws, never hangs.
    public static async Task<IReadOnlyList<MemoryNote>> RetrieveAsync(CancellationToken ct)
    {
        if (!Installed) return Array.Empty<MemoryNote>();
        return ToMemoryNotes(await RunAsync(BuildRecentArgs(RetrieveK), ct).ConfigureAwait(false));
    }

    private static async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var (ok, stdout) = await RunRawAsync(args, ct).ConfigureAwait(false);
        return ok ? ParseMemoryJson(stdout) : Array.Empty<string>();
    }

    // Spawn `uv run python memory_db.py <args>` one-shot and return (exit==0, stdout). 20s cap + KillTree; never
    // throws/hangs. The shared runner behind both the recall read path and the editable-admin save/list/delete.
    private static async Task<(bool ok, string stdout)> RunRawAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        using Process? p = StartOrNull(args);
        try
        {
            if (p is null) return (false, "");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            return (p.ExitCode == 0, stdout);
        }
        catch (OperationCanceledException) { KillTree(p); return (false, ""); }
        catch { KillTree(p); return (false, ""); }
    }

    private static Process? StartOrNull(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("uv")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoPaths.Root,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("python");
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    private static void KillTree(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch { /* already exited / no longer killable — best-effort */ }
    }

    // PURE: parse memory_db.py's JSON array of {id, content, tags, ts} to the content strings (the facts). A
    // malformed / non-array input, or a row with blank content, is dropped — never throws, so garbage stdout
    // degrades to "no memory injected".
    public static IReadOnlyList<string> ParseMemoryJson(string? json)
    {
        var rows = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(content)) rows.Add(content);
            }
        }
        catch { return new List<string>(); }
        return rows;
    }

    // PURE: map the memory content strings to the MemoryNotes ChatPrompt.Build injects. The content IS the fact, so
    // each note is key-less (rendered "- <fact>" by MemoryBlock). Empty in => empty out (no injection).
    public static IReadOnlyList<MemoryNote> ToMemoryNotes(IReadOnlyList<string> contents)
        => contents is { Count: > 0 }
            ? contents.Select(c => new MemoryNote("", c)).ToList()
            : Array.Empty<MemoryNote>();

    // ---- editable-memory admin (the explicit "editable memory agent": /api/memory save/list/delete + a future
    //      panel). Memory notes are SHORT facts, so save passes content as a single argv via ArgumentList (no shell,
    //      proper escaping). Pure argv/parse seams are unit-tested; the async methods share the degrade contract. ----

    // PURE: argv for `uv run python memory_db.py save CONTENT TAGS` / `delete ID`.
    public static IReadOnlyList<string> BuildSaveArgs(string content, string? tags)
        => new[] { Script, "save", content ?? "", tags ?? "" };

    public static IReadOnlyList<string> BuildDeleteArgs(int id)
        => new[] { Script, "delete", id.ToString() };

    // PURE: read the {"id":N} memory_db save prints; 0 on anything malformed (never throws).
    public static int ParseSavedId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        }
        catch { /* not JSON */ }
        return 0;
    }

    // PURE: parse memory_db's JSON array to editable MemoryRecords (id + content + tags). Blank-content rows and any
    // malformed/non-array input are dropped (never throws). The admin list needs ids (to edit/delete), unlike recall.
    public static IReadOnlyList<MemoryRecord> ParseMemoryRecords(string? json)
    {
        var rows = new List<MemoryRecord>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(content)) continue;
                var id = el.TryGetProperty("id", out var iv) && iv.ValueKind == JsonValueKind.Number && iv.TryGetInt32(out var ii) ? ii : 0;
                var tags = el.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                rows.Add(new MemoryRecord(id, content, tags));
            }
        }
        catch { return new List<MemoryRecord>(); }
        return rows;
    }

    // Save one memory note; returns its new id, or 0 on blank content / missing sidecar / failure. Never throws/hangs.
    public static async Task<int> SaveAsync(string content, string? tags, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content) || !File.Exists(Script)) return 0;
        var (ok, stdout) = await RunRawAsync(BuildSaveArgs(content, tags), ct).ConfigureAwait(false);
        return ok ? ParseSavedId(stdout) : 0;
    }

    // List the editable memory rows (newest-first); empty on any failure. Gated on the store existing (Installed).
    public static async Task<IReadOnlyList<MemoryRecord>> ListAsync(int limit, CancellationToken ct)
    {
        if (!Installed) return Array.Empty<MemoryRecord>();
        var (ok, stdout) = await RunRawAsync(BuildRecentArgs(limit), ct).ConfigureAwait(false);
        return ok ? ParseMemoryRecords(stdout) : Array.Empty<MemoryRecord>();
    }

    // Delete one memory row by id; false on any failure (best-effort, never throws/hangs).
    public static async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        if (!File.Exists(Script)) return false;
        var (ok, _) = await RunRawAsync(BuildDeleteArgs(id), ct).ConfigureAwait(false);
        return ok;
    }
}
