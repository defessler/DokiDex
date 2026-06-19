using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// code_search sidecar — semantic RAG over THIS repo's indexed source, via serving/memory-mcp/code_index.py's
// `search` subcommand (added to its __main__), run one-shot through `uv run python ...`. Unlike the web tool
// this is OUR script, so it prints the JSON array of {path,start_line,end_line,content,score} straight to
// STDOUT (no temp file). It has only stdlib deps, so a plain `uv run python <script> search Q K` needs no extra.
//
// HARD runtime deps (degrade, never throw): the index must be built (code_index.db present) AND the :8090 embed
// server must be running (a SEPARATE always-on CPU server, NOT auto-started with chat). When either is missing
// the live exec exits non-zero (connection-refused / no rows) and we return a short "code search unavailable —
// start the embed server / build the index" message — the same contract as server.py's code_search.
//
// Three pure seams (unit-tested, no process): BuildArgs, ParseCodeJson, and the bounded FormatCodeResults
// (chunks are long and re-sent every hop of the agent loop, so each is truncated).
public static class CodeSearch
{
    private const int MaxResults = 5;        // chunks folded into the tool text
    private const int MaxChunkChars = 400;   // per-chunk cap (chunks are long; re-sent every hop)

    private static string Script => Path.Combine(RepoPaths.Root, "serving", "memory-mcp", "code_index.py");
    private static string Db => Path.Combine(RepoPaths.Root, "serving", "memory-mcp", "code_index.db");

    // The index DB must exist before a search is meaningful. The embed server's availability isn't probed here
    // (it's a live HTTP dep) — a down server makes the script exit non-zero and we degrade in SearchAsync.
    public static bool Installed => File.Exists(Script) && File.Exists(Db);

    // Pure: the argv passed to `uv run python` — the script path, the `search` subcommand, the query, and k.
    // k is clamped to [1,10] so a sloppy model argument can't ask for a huge result set.
    public static IReadOnlyList<string> BuildArgs(string query, int k)
    {
        var n = Math.Clamp(k, 1, 10);
        return new[] { Script, "search", query ?? "", n.ToString() };
    }

    public sealed record Result(bool Ok, IReadOnlyList<(string path, int start, int end, string content, double score)> Rows, string? Message);

    public static async Task<Result> SearchAsync(string query, int k, CancellationToken ct)
    {
        if (!Installed) return new Result(false, Array.Empty<(string, int, int, string, double)>(),
            "code search unavailable — the code index isn't built. Build it with:  doki index");
        // `using` so the handle is always disposed; declared OUTSIDE the try so the catch can KILL a still-running
        // child when the 30s cap (or caller ct) fires — otherwise every chat-hop timeout leaks an orphaned
        // uv/python process (a real leak in the interactive loop, which may call this on each of its 4 hops).
        using Process? p = StartOrNull(BuildArgs(query, k));
        try
        {
            if (p is null) return new Result(false, Array.Empty<(string, int, int, string, double)>(), "could not start uv/python");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            var err = await errTask.ConfigureAwait(false);

            // Embed server down / no index => non-zero exit (connection-refused). Degrade, never throw.
            if (p.ExitCode != 0)
                return new Result(false, Array.Empty<(string, int, int, string, double)>(),
                    "code search unavailable — start the embed server (start-embed.ps1) and build the index (doki index).");

            return new Result(true, ParseCodeJson(stdout), "done");
        }
        catch (OperationCanceledException) { KillTree(p); return new Result(false, Array.Empty<(string, int, int, string, double)>(), "code search cancelled / timed out"); }
        catch (Exception ex) { KillTree(p); return new Result(false, Array.Empty<(string, int, int, string, double)>(), $"code search error: {ex.Message}"); }
    }

    // `uv run python <script> search Q K` — uv resolves the project python; the script is stdlib-only.
    private static Process? StartOrNull(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("uv")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,   // the script prints its JSON array to stdout
            RedirectStandardError = true,
            WorkingDirectory = RepoPaths.Root,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("python");
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    // Kill a still-running child (and its whole tree — `uv run` spawns python under itself) when the timeout or
    // caller cancellation fires, so a wedged sidecar can't outlive the call. Guarded: killing an already-exited
    // process throws, and that's fine — best-effort.
    private static void KillTree(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch { /* already exited / no longer killable — best-effort */ }
    }

    // PURE: parse the JSON array of { path, start_line, end_line, content, score } the script prints. Any
    // malformed / non-array input yields an empty list (never throws), so a garbage stdout degrades to "no
    // matching code". start/end/score tolerate missing or string-typed numbers.
    public static IReadOnlyList<(string path, int start, int end, string content, double score)> ParseCodeJson(string? json)
    {
        var rows = new List<(string, int, int, string, double)>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                rows.Add((Str(el, "path"), Int(el, "start_line"), Int(el, "end_line"), Str(el, "content"), Dbl(el, "score")));
            }
        }
        catch { return new List<(string, int, int, string, double)>(); }
        return rows;
    }

    private static string Str(JsonElement o, string n)
        => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement o, string n)
    {
        if (!o.TryGetProperty(n, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static double Dbl(JsonElement o, string n)
    {
        if (!o.TryGetProperty(n, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    // PURE + bounded: render the top chunks into a compact tool-result text. Each chunk is clipped to
    // MaxChunkChars and at most MaxResults items shown so the block stays small across the loop's hops. An empty
    // set renders a clean "no matching code" line (the degrade path also routes here).
    public static string FormatCodeResults(string query, IReadOnlyList<(string path, int start, int end, string content, double score)> rows)
    {
        if (rows is null || rows.Count == 0)
            return $"no matching code for \"{query}\" (is the index built and the embed server running?).";

        var shown = rows.Take(MaxResults).ToList();
        var sb = new StringBuilder();
        sb.Append(shown.Count).Append(" code chunk(s) for \"").Append(query).Append("\":");
        foreach (var (path, start, end, content, score) in shown)
        {
            sb.Append("\n- ").Append(path).Append(':').Append(start).Append('-').Append(end)
              .Append("  (score ").Append(score.ToString("0.###")).Append(')');
            var preview = Truncate(content, MaxChunkChars);
            if (!string.IsNullOrWhiteSpace(preview)) sb.Append('\n').Append(preview);
        }
        return sb.ToString();
    }

    private static string Truncate(string? s, int cap)
    {
        s ??= "";
        return s.Length <= cap ? s : s[..cap] + "…";
    }
}
