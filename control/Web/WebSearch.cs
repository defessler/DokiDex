using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DokiDex.Web;

// web_search sidecar — DuckDuckGo via the `ddgs` CLI run one-shot through `uvx` (the same uvx the Crush harness
// already depends on). EMPIRICALLY VERIFIED contract (ddgs 9.x): `ddgs text -q Q -m N -o json` with a BARE `-o`
// writes an auto-named file in CWD and prints NOTHING to stdout; so we pass an EXPLICIT `-o <tempfile>.json`,
// then read+parse+delete that file — exactly the SAM/Demucs temp-output-file pattern. On zero results ddgs
// exits NON-ZERO (RC=2) with the message on stderr, so we degrade gracefully (never throw, never hang).
//
// Three seams: a pure BuildArgs (unit-tested), a pure ParseDdgsJson (the verified {title,href,body} array ->
// our (title,url,snippet), unit-tested), and a pure bounded FormatWebResults (truncates long bodies that would
// otherwise re-inflate the tool text on every hop of the agent loop). The live exec degrades when uvx/network
// is absent — Result(false, ...) carries a short "search unavailable" message.
public static class WebSearch
{
    private const int MaxResults = 5;     // result count folded into the tool text (kept small for the loop)
    private const int MaxSnippetChars = 200;   // per-item snippet cap (bodies are long; re-sent every hop)

    // Pure: the ddgs argv. The query/count/out path are discrete argv (no shell concat, so spaces are safe).
    // k is clamped to [1,10] so a sloppy model argument can't ask for a huge fetch. `-b auto` lets ddgs pick a
    // working backend (bing/brave/google/duckduckgo) — it falls back across them on a transient rate-limit.
    public static IReadOnlyList<string> BuildArgs(string query, int k, string outJsonPath)
    {
        var n = Math.Clamp(k, 1, 10);
        return new[] { "ddgs", "text", "-q", query ?? "", "-m", n.ToString(), "-o", outJsonPath, "-b", "auto" };
    }

    public sealed record Result(bool Ok, IReadOnlyList<(string title, string url, string snippet)> Rows, string? Message);

    public static async Task<Result> SearchAsync(string query, int k, CancellationToken ct)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"dokidex-ddg-{Guid.NewGuid():N}.json");
        // `using` so the handle is always disposed; declared OUTSIDE the try so the catch can KILL a still-running
        // child when the 20s cap (or caller ct) fires — otherwise every chat-hop timeout leaks an orphaned
        // uvx/ddgs process (a real leak in the interactive loop, which may call this on each of its 4 hops).
        using Process? p = StartOrNull(BuildArgs(query, k, outFile));
        try
        {
            if (p is null) return new Result(false, Array.Empty<(string, string, string)>(), "could not start uvx/ddgs");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            // Mirror CodeSearch: start the stderr read WITHOUT awaiting, then WaitForExitAsync(cts.Token) so the
            // 20s cap governs the WHOLE exec. A wedged sidecar (stuck DNS/socket, no progress) never reaches stderr
            // EOF; cts fires at 20s, WaitForExit throws OCE, and we route to the graceful "cancelled / timed out".
            var errTask = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var err = await errTask.ConfigureAwait(false);

            // Zero results => ddgs exits non-zero / writes no file: treat as "no results", degrade, never throw.
            if (p.ExitCode != 0 || !File.Exists(outFile))
                return new Result(false, Array.Empty<(string, string, string)>(),
                    $"web search returned no results (is uvx/ddgs installed and the network reachable?) {err}".Trim());

            var rows = ParseDdgsJson(File.ReadAllText(outFile));
            return new Result(true, rows, "done");
        }
        catch (OperationCanceledException) { KillTree(p); return new Result(false, Array.Empty<(string, string, string)>(), "web search cancelled / timed out"); }
        catch (Exception ex) { KillTree(p); return new Result(false, Array.Empty<(string, string, string)>(), $"web search error: {ex.Message}"); }
        finally { try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* best-effort cleanup */ } }
    }

    private static Process? StartOrNull(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("uvx") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    // Kill a still-running child (and its whole tree — uvx spawns the real python under itself) when the timeout
    // or caller cancellation fires, so a wedged sidecar can't outlive the call. Guarded: killing an already-exited
    // process throws, and that's fine — best-effort.
    private static void KillTree(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch { /* already exited / no longer killable — best-effort */ }
    }

    // PURE: parse the ddgs JSON array of { title, href, body } into our (title, url, snippet) tuples, mapping
    // href -> url and body -> snippet (the VERIFIED field names). Any malformed / non-array / missing-field input
    // yields an empty list — never throws, so a transient garbage body degrades to "no results".
    public static IReadOnlyList<(string title, string url, string snippet)> ParseDdgsJson(string? json)
    {
        var rows = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                rows.Add((Str(el, "title"), Str(el, "href"), Str(el, "body")));
            }
        }
        catch { return new List<(string, string, string)>(); }
        return rows;
    }

    private static string Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // PURE + bounded: render the top results into a compact tool-result text. Each snippet is clipped to
    // MaxSnippetChars and at most MaxResults items are shown, so the block stays small across the agent loop's
    // repeated hops. An empty set renders a clean "no web results" line (the degrade path also routes here).
    public static string FormatWebResults(string query, IReadOnlyList<(string title, string url, string snippet)> rows)
    {
        if (rows is null || rows.Count == 0)
            return $"no web results for \"{query}\" (search unavailable or nothing found).";

        var shown = rows.Take(MaxResults).ToList();
        var sb = new StringBuilder();
        sb.Append(shown.Count).Append(" web result(s) for \"").Append(query).Append("\":");
        foreach (var (title, url, snippet) in shown)
        {
            sb.Append("\n- ").Append(string.IsNullOrWhiteSpace(title) ? url : title);
            if (!string.IsNullOrWhiteSpace(url)) sb.Append(" <").Append(url).Append('>');
            var preview = Truncate(snippet, MaxSnippetChars);
            if (!string.IsNullOrWhiteSpace(preview)) sb.Append("\n  ").Append(preview);
        }
        return sb.ToString();
    }

    private static string Truncate(string? s, int cap)
    {
        s ??= "";
        return s.Length <= cap ? s : s[..cap] + "…";
    }
}
