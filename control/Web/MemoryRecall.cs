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
        using Process? p = StartOrNull(args);
        try
        {
            if (p is null) return Array.Empty<string>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            if (p.ExitCode != 0) return Array.Empty<string>();
            return ParseMemoryJson(stdout);
        }
        catch (OperationCanceledException) { KillTree(p); return Array.Empty<string>(); }
        catch { KillTree(p); return Array.Empty<string>(); }
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
}
