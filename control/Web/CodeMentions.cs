using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DokiDex.Web;

// `@rel/path` file mentions (1.7a): lets the user reference a workspace file inline — "fix the bug in @src/app.cs"
// — without spending a Read tool round-trip first. Preprocessing happens BEFORE the text becomes the turn's user
// message, for BOTH interactive and one-shot runs: Program.cs calls Augment() on the raw text and appends the
// result to that SAME string (`text + Augment(root, text)`) before handing it to CodeAgent.RunTurnAsync — the
// original "@token" wording stays exactly where the user typed it, so the model sees what was actually referenced.
// Mirrors CodeSessions' shape: a few PURE, disk-free helpers (ExtractMentions, StripTrailingPunctuation,
// BuildAppendix — the unit-test seam) plus two small disk-touching steps (Resolve, Augment) kept as thin as
// possible.
public static class CodeMentions
{
    public const int MaxMentions = 3;          // more than this in one message is almost always a typo/paste accident
    public const int WindowLines = 200;        // "first ~200 lines" per the plan
    public const int MaxAppendChars = 8_000;   // hard cap on one mention's appended window

    // Recognizes an '@'-prefixed path-ish token at a WORD BOUNDARY — '@' at the very start of the input, or right
    // after whitespace — so "user@example.com" is never mistaken for a mention (there the '@' is preceded by 'r',
    // not whitespace/start-of-string). The token is one-or-more path-ish characters: letters, digits, '/', '\',
    // '.', '-', '_'. Note this greedily swallows a trailing '.' that's really sentence punctuation (e.g. "see
    // @README.md.") — sorting that out is StripTrailingPunctuation + Resolve's as-is-then-stripped retry below, NOT
    // this regex, which stays a plain, unconditional tokenizer. A comma can never appear IN a token (it's not in
    // the class), so "@a.txt, then" already stops cleanly at the comma with no extra handling needed.
    private static readonly Regex MentionRx = new(@"(?<=^|\s)@[A-Za-z0-9/\\.\-_]+", RegexOptions.Compiled);

    // PURE: every @-prefixed path token in `input`, IN ORDER, leading '@' stripped — UNBOUNDED (the MaxMentions cap
    // and its "N more skipped" note are Augment's job below; only Augment knows how many were actually resolved).
    // Empty/no-match input => an empty list, never null. Total + side-effect-free — the primary unit-test seam.
    public static IReadOnlyList<string> ExtractMentions(string input)
    {
        if (string.IsNullOrEmpty(input)) return Array.Empty<string>();
        var result = new List<string>();
        foreach (Match m in MentionRx.Matches(input))
            result.Add(m.Value[1..]);
        return result;
    }

    // PURE: drop a single trailing '.' or ',' — sentence punctuation a greedy token capture can't tell apart from a
    // real path character up front. A one-character token (just "." or ",") is left alone — nothing sensible would
    // remain. Called by Resolve ONLY as a fallback once the as-is token has already failed to resolve, never
    // unconditionally, so a file literally named "notes." is still found on the first try.
    internal static string StripTrailingPunctuation(string token)
        => token.Length > 1 && (token[^1] == '.' || token[^1] == ',') ? token[..^1] : token;

    // One resolved mention, ready for BuildAppendix. `Token` is the raw text as extracted (sans '@'); `Path` is the
    // path actually used to find the file (== Token, unless the trailing-punctuation retry fired); `Content` is the
    // rendered file window when Found, else null.
    public sealed record Resolved(string Token, string Path, bool Found, string? Content);

    // PURE: assemble the text to APPEND after the user's original message from an already-resolved batch — the
    // pipeline's disk work happens in Resolve/Augment below; this just formats. Per the plan's exact shapes: a
    // found mention becomes "\n\n[file: <rel>]\n<window>"; a not-found one becomes "\n[@<path>: not found in
    // workspace]". `skipped` (mentions beyond MaxMentions, never even attempted) adds one trailing note when > 0.
    // Returns "" when there's nothing to say at all (no resolved mentions, nothing skipped) — the caller then
    // appends nothing.
    public static string BuildAppendix(IReadOnlyList<Resolved> resolved, int skipped)
    {
        if (resolved.Count == 0 && skipped <= 0) return "";
        var sb = new StringBuilder();
        foreach (var r in resolved)
            sb.Append(r.Found ? $"\n\n[file: {r.Path}]\n{r.Content}" : $"\n[@{r.Path}: not found in workspace]");
        if (skipped > 0)
            sb.Append($"\n[{skipped} more @mention(s) skipped — max {MaxMentions} file mentions per message]");
        return sb.ToString();
    }

    // The thin disk step for ONE token: try it AS-IS through CodeTools' workspace gate + a File.Exists check first;
    // if that fails and the token ends with '.'/',', retry once with StripTrailingPunctuation applied (the
    // "@README.md" at the end of a sentence" case). Never throws — any IO failure degrades to Found=false, same as
    // a plain missing file (the model only needs to know the reference didn't pan out, not why).
    internal static Resolved Resolve(string root, string token)
    {
        var direct = TryRead(root, token);
        if (direct is not null) return new Resolved(token, token, true, direct);

        var stripped = StripTrailingPunctuation(token);
        if (stripped != token)
        {
            var retried = TryRead(root, stripped);
            if (retried is not null) return new Resolved(token, stripped, true, retried);
        }
        return new Resolved(token, token, false, null);
    }

    private static string? TryRead(string root, string path)
    {
        var full = CodeTools.ResolveWorkspacePath(root, path);
        if (full is null || !File.Exists(full)) return null;
        try
        {
            var lines = File.ReadAllLines(full);
            return CodeTools.FormatFileWindow(path, lines, offset: 1, limit: WindowLines, maxChars: MaxAppendChars);
        }
        catch { return null; }
    }

    // The impure entry point Program.cs calls once per user turn (interactive AND one-shot), on the RAW text before
    // it becomes the turn's message: extract every mention, take the first MaxMentions (excess counted as
    // `skipped`), resolve each, and assemble. Returns just the text to APPEND — "" when the input has no mentions at
    // all — the caller does `text + Augment(root, text)`, leaving the original "@token" wording untouched.
    public static string Augment(string root, string input)
    {
        var tokens = ExtractMentions(input);
        if (tokens.Count == 0) return "";
        var taken = tokens.Take(MaxMentions).ToList();
        var skipped = tokens.Count - taken.Count;
        var resolved = taken.Select(t => Resolve(root, t)).ToList();
        return BuildAppendix(resolved, skipped);
    }
}
