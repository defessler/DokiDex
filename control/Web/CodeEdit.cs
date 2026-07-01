using System;
using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// The SEARCH/REPLACE edit applier — the crux of reliable edits from weak local models (the cross-tool consensus
// from Aider/Cline/Roo: removing the fuzzy stages causes ~9× more edit errors). Given a file's content plus a
// SEARCH block and a REPLACE block, locate the search lines and produce the edited content, trying progressively
// looser strategies (exact whole-line → whitespace-flexible → [future] similarity) and returning a reflection-
// friendly error when nothing matches so the agent loop can re-read and retry. Pure + total — no disk.
//
// LINE-BASED on purpose: matching is done over contiguous WHOLE-LINE windows, not raw substrings, so a SEARCH with
// less indentation than the file can't partial-match inside the file's leading whitespace and corrupt it. The
// model supplies complete lines (the SEARCH/REPLACE convention); CRLF/LF and a trailing newline are preserved.
public static class CodeEdit
{
    // Ok + the new content on success; on failure Ok=false, the ORIGINAL content (unchanged), and a model-facing
    // Error that says how to recover. Strategy names which ladder rung matched (or "none") — for diagnostics/UI.
    public sealed record EditOutcome(bool Ok, string NewContent, string? Error, string Strategy);

    public static EditOutcome ApplyEdit(string content, string search, string replace)
    {
        content ??= "";
        search ??= "";
        replace ??= "";
        if (search.Trim().Length == 0)
            return new(false, content, "The SEARCH text is empty — provide the exact lines to replace.", "none");

        var nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var contentLines = SplitLines(content);
        var searchLines = SplitLines(search); TrimTrailingBlank(searchLines);
        var replaceLines = SplitLines(replace); TrimTrailingBlank(replaceLines);
        if (searchLines.Count == 0)
            return new(false, content, "The SEARCH text is empty — provide the exact lines to replace.", "none");

        // 1) EXACT whole-line block, and UNIQUE. Ambiguity is a real risk with weak models, so (like Claude Code's
        //    Edit) a SEARCH appearing more than once is rejected with a recover-by-adding-context message.
        var exact = FindWindows(contentLines, searchLines, ignoreWs: false);
        if (exact.Count == 1) return Splice(contentLines, exact[0], searchLines.Count, replaceLines, nl, "exact");
        if (exact.Count > 1)
            return new(false, content,
                "The SEARCH text matched in multiple places — include more surrounding lines so it is unique.", "exact");

        // 2) WHITESPACE-FLEXIBLE: match the SEARCH lines ignoring each line's leading/trailing whitespace (the most
        //    common weak-model drift), still requiring a UNIQUE window. REPLACE is spliced in verbatim.
        var ws = FindWindows(contentLines, searchLines, ignoreWs: true);
        if (ws.Count == 1) return Splice(contentLines, ws[0], searchLines.Count, replaceLines, nl, "whitespace");
        if (ws.Count > 1)
            return new(false, content,
                "The SEARCH text matched multiple places (ignoring indentation) — add more surrounding lines so it is unique.", "whitespace");

        return new(false, content,
            "SEARCH text not found (even ignoring indentation). Re-read the file with Read and copy the exact lines to change.",
            "none");
    }

    // PURE: a compact, Claude-Code-style diff of an edit for the approval PREVIEW and the CLI display. Finds the
    // changed region via common leading/trailing lines and shows up to `context` unchanged lines around it, the
    // removed lines prefixed "- ", the added prefixed "+ ", unchanged context "  ". Identical text => "(no changes)".
    // The console applies red/green color; this stays plain text. Total + side-effect-free.
    public static string RenderDiff(string path, string oldText, string newText, int context = 3)
    {
        oldText ??= "";
        newText ??= "";
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return "(no changes)";

        var oldL = SplitLines(oldText); TrimTrailingBlank(oldL);
        var newL = SplitLines(newText); TrimTrailingBlank(newL);

        var pre = 0;
        while (pre < oldL.Count && pre < newL.Count && oldL[pre] == newL[pre]) pre++;
        var suf = 0;
        while (suf < oldL.Count - pre && suf < newL.Count - pre
               && oldL[oldL.Count - 1 - suf] == newL[newL.Count - 1 - suf]) suf++;

        var sb = new System.Text.StringBuilder();
        sb.Append(path).Append('\n');
        for (var i = Math.Max(0, pre - context); i < pre; i++) sb.Append("  ").Append(oldL[i]).Append('\n');
        for (var i = pre; i < oldL.Count - suf; i++) sb.Append("- ").Append(oldL[i]).Append('\n');
        for (var i = pre; i < newL.Count - suf; i++) sb.Append("+ ").Append(newL[i]).Append('\n');
        var afterStart = oldL.Count - suf;
        for (var i = afterStart; i < Math.Min(oldL.Count, afterStart + context); i++) sb.Append("  ").Append(oldL[i]).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    // One SEARCH/REPLACE edit block parsed from a model's text content. Path is workspace-relative.
    public sealed record SearchReplaceBlock(string Path, string Search, string Replace);

    // PURE: extract SEARCH/REPLACE edit blocks from a model's text content — the Aider / Mistral-Vibe protocol that
    // open coder models emit far more reliably than JSON edit-args (triple-confirmed: Aider, Cline/Roo, Mistral's own
    // Vibe CLI). Each block is a file path on its own line, then:
    //     <<<<<<< SEARCH \n <old lines> \n ======= \n <new lines> \n >>>>>>> REPLACE
    // Path = the nearest non-blank line above the SEARCH marker (code-fence / backticks stripped). The divider must
    // be a line of ONLY '=' so a code line like `a == b` can't split the block. A block missing its divider or
    // REPLACE marker, or with no resolvable path, is skipped. Total + side-effect-free — the unit-test seam.
    public static IReadOnlyList<SearchReplaceBlock> ParseSearchReplaceBlocks(string content)
    {
        var blocks = new List<SearchReplaceBlock>();
        if (string.IsNullOrEmpty(content)) return blocks;
        var lines = SplitLines(content);
        var i = 0;
        while (i < lines.Count)
        {
            if (!IsSearchStart(lines[i])) { i++; continue; }

            // path = nearest non-blank, non-bare-fence line above the SEARCH marker
            var path = "";
            for (var k = i - 1; k >= 0; k--)
            {
                var t = lines[k].Trim();
                if (t.Length == 0) continue;
                var p = CleanPathLine(t);
                if (p.Length == 0) continue;   // a bare ``` / ```lang fence — keep climbing
                path = p; break;
            }

            i++;                                       // past <<<<<<< SEARCH
            var searchStart = i;
            while (i < lines.Count && !IsDivider(lines[i])) i++;
            if (i >= lines.Count) break;               // malformed — no divider
            var search = string.Join("\n", lines.GetRange(searchStart, i - searchStart));
            i++;                                       // past =======
            var replaceStart = i;
            while (i < lines.Count && !IsReplaceEnd(lines[i])) i++;
            if (i >= lines.Count) break;               // malformed — no REPLACE end
            var replace = string.Join("\n", lines.GetRange(replaceStart, i - replaceStart));
            i++;                                       // past >>>>>>> REPLACE

            if (path.Length > 0) blocks.Add(new SearchReplaceBlock(path, search, replace));
        }
        return blocks;
    }

    private static bool IsSearchStart(string line) => line.TrimStart().StartsWith("<<<<<<<", StringComparison.Ordinal);
    private static bool IsReplaceEnd(string line) => line.TrimStart().StartsWith(">>>>>>>", StringComparison.Ordinal);
    private static bool IsDivider(string line)
    {
        var t = line.Trim();
        return t.Length >= 3 && t.All(c => c == '=');
    }

    // Strip a code-fence prefix (``` or ```lang) and surrounding backticks/quotes from a candidate path line; return
    // "" for a bare fence (no path on it) so the caller keeps climbing to the real path line.
    private static string CleanPathLine(string t)
    {
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            t = t[3..].Trim();
            var sp = t.IndexOf(' ');
            if (sp >= 0) t = t[(sp + 1)..].Trim();                                  // ```lang path -> path
            else if (!t.Contains('.') && !t.Contains('/') && !t.Contains('\\')) t = "";  // ```lang (no path)
        }
        return t.Trim('`', '"', '\'', ' ');
    }

    // Split into lines WITHOUT terminators, normalizing CRLF→LF first. A trailing newline yields a trailing ""
    // element, so re-joining reproduces the original trailing newline exactly (no double/lost newline).
    private static List<string> SplitLines(string s) => s.Replace("\r\n", "\n").Split('\n').ToList();

    private static void TrimTrailingBlank(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
    }

    // All start indices where `needle` matches a contiguous WHOLE-LINE window of `hay` — exact, or (ignoreWs) with
    // each line's leading/trailing whitespace trimmed before comparison. Total + side-effect-free.
    private static List<int> FindWindows(List<string> hay, List<string> needle, bool ignoreWs)
    {
        var hits = new List<int>();
        if (needle.Count == 0 || needle.Count > hay.Count) return hits;
        for (var i = 0; i + needle.Count <= hay.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++)
            {
                var a = ignoreWs ? hay[i + j].Trim() : hay[i + j];
                var b = ignoreWs ? needle[j].Trim() : needle[j];
                if (!string.Equals(a, b, StringComparison.Ordinal)) { ok = false; break; }
            }
            if (ok) hits.Add(i);
        }
        return hits;
    }

    // Replace the [start, start+searchLen) line window with replaceLines, re-joining with the file's newline style.
    private static EditOutcome Splice(List<string> contentLines, int start, int searchLen, List<string> replaceLines, string nl, string strategy)
    {
        var result = new List<string>(contentLines.Count - searchLen + replaceLines.Count);
        result.AddRange(contentLines.Take(start));
        result.AddRange(replaceLines);
        result.AddRange(contentLines.Skip(start + searchLen));
        return new EditOutcome(true, string.Join(nl, result), null, strategy);
    }
}
