using System;
using System.IO;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// 1.7a — `@rel/path` file mentions. ExtractMentions/StripTrailingPunctuation/BuildAppendix are PURE (no disk) — the
// primary TDD seam; Resolve/Augment are the thin disk-touching steps, exercised here against a real scratch temp
// dir (same pattern as CodeAgentTests' PrepareEdit tests).
public class CodeMentionsTests
{
    // ---- ExtractMentions (pure) ----

    [Fact]
    public void ExtractMentions_returns_empty_for_plain_text_with_no_mentions()
        => Assert.Empty(CodeMentions.ExtractMentions("just a normal message, nothing special here."));

    [Fact]
    public void ExtractMentions_extracts_a_single_mention()
    {
        var got = CodeMentions.ExtractMentions("please fix the bug in @src/app.cs today");
        Assert.Equal(new[] { "src/app.cs" }, got);
    }

    [Fact]
    public void ExtractMentions_extracts_a_mention_at_the_very_start_of_the_input()
        => Assert.Equal(new[] { "README.md" }, CodeMentions.ExtractMentions("@README.md summarize this"));

    [Fact]
    public void ExtractMentions_extracts_more_than_three_uncapped_the_cap_is_Augments_job()
    {
        var got = CodeMentions.ExtractMentions("@a.txt @b.txt @c.txt @d.txt");
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "d.txt" }, got);
    }

    [Fact]
    public void ExtractMentions_does_not_treat_a_mid_word_at_as_a_mention()
        // "user@example.com" — the '@' is preceded by 'r', not whitespace/start, so this is NOT a mention.
        => Assert.Empty(CodeMentions.ExtractMentions("email me at user@example.com please"));

    [Fact]
    public void ExtractMentions_still_finds_a_real_mention_next_to_an_email()
    {
        var got = CodeMentions.ExtractMentions("cc user@example.com and also check @notes/todo.md");
        Assert.Equal(new[] { "notes/todo.md" }, got);
    }

    [Fact]
    public void ExtractMentions_preserves_backslash_paths()
        => Assert.Equal(new[] { "control\\Web\\CodeTools.cs" },
            CodeMentions.ExtractMentions(@"look at @control\Web\CodeTools.cs closely"));

    [Fact]
    public void ExtractMentions_stops_a_token_at_a_comma_since_comma_is_not_path_ish()
        => Assert.Equal(new[] { "a.txt" }, CodeMentions.ExtractMentions("see @a.txt, then done"));

    // ---- StripTrailingPunctuation (pure) ----

    [Fact]
    public void StripTrailingPunctuation_strips_a_single_trailing_dot_or_comma()
    {
        Assert.Equal("README.md", CodeMentions.StripTrailingPunctuation("README.md."));
        Assert.Equal("README.md", CodeMentions.StripTrailingPunctuation("README.md,"));
    }

    [Fact]
    public void StripTrailingPunctuation_leaves_a_token_with_no_trailing_punctuation_unchanged()
        => Assert.Equal("README.md", CodeMentions.StripTrailingPunctuation("README.md"));

    [Fact]
    public void StripTrailingPunctuation_leaves_a_lone_punctuation_character_unchanged()
        => Assert.Equal(".", CodeMentions.StripTrailingPunctuation("."));

    // ---- BuildAppendix (pure) ----

    [Fact]
    public void BuildAppendix_formats_a_found_mention()
    {
        var resolved = new[] { new CodeMentions.Resolved("README.md", "README.md", true, "1\thello\n") };
        var text = CodeMentions.BuildAppendix(resolved, skipped: 0);
        Assert.Equal("\n\n[file: README.md]\n1\thello\n", text);
    }

    [Fact]
    public void BuildAppendix_formats_a_not_found_mention()
    {
        var resolved = new[] { new CodeMentions.Resolved("nope.xyz", "nope.xyz", false, null) };
        var text = CodeMentions.BuildAppendix(resolved, skipped: 0);
        Assert.Equal("\n[@nope.xyz: not found in workspace]", text);
    }

    [Fact]
    public void BuildAppendix_notes_skipped_mentions()
    {
        var text = CodeMentions.BuildAppendix(Array.Empty<CodeMentions.Resolved>(), skipped: 2);
        Assert.Contains("2 more @mention(s) skipped", text);
        Assert.Contains("max 3", text);
    }

    [Fact]
    public void BuildAppendix_returns_empty_when_nothing_to_say()
        => Assert.Equal("", CodeMentions.BuildAppendix(Array.Empty<CodeMentions.Resolved>(), skipped: 0));

    [Fact]
    public void BuildAppendix_combines_found_not_found_and_skipped_in_order()
    {
        var resolved = new[]
        {
            new CodeMentions.Resolved("a.txt", "a.txt", true, "1\thi\n"),
            new CodeMentions.Resolved("nope.xyz", "nope.xyz", false, null),
        };
        var text = CodeMentions.BuildAppendix(resolved, skipped: 1);
        Assert.Equal(
            "\n\n[file: a.txt]\n1\thi\n" +
            "\n[@nope.xyz: not found in workspace]" +
            "\n[1 more @mention(s) skipped — max 3 file mentions per message]",
            text);
    }

    // ---- Resolve / Augment (thin disk step — real scratch temp dir) ----

    private static string NewScratchDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doki-cm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Augment_returns_empty_for_plain_text_with_no_mentions()
    {
        var dir = NewScratchDir();
        try { Assert.Equal("", CodeMentions.Augment(dir, "just chatting, no files here")); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Augment_appends_a_bounded_file_window_for_an_existing_file()
    {
        var dir = NewScratchDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "line one\nline two\n");
            var appended = CodeMentions.Augment(dir, "summarize @README.md for me");
            Assert.Contains("[file: README.md]", appended);
            Assert.Contains("line one", appended);
            Assert.Contains("line two", appended);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Augment_appends_a_not_found_note_for_a_missing_file()
    {
        var dir = NewScratchDir();
        try
        {
            var appended = CodeMentions.Augment(dir, "look at @nope.xyz please");
            Assert.Equal("\n[@nope.xyz: not found in workspace]", appended);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Augment_caps_at_three_mentions_and_notes_the_rest_as_skipped()
    {
        var dir = NewScratchDir();
        try
        {
            var appended = CodeMentions.Augment(dir, "@a.txt @b.txt @c.txt @d.txt");
            Assert.Contains("[@a.txt: not found in workspace]", appended);
            Assert.Contains("[@b.txt: not found in workspace]", appended);
            Assert.Contains("[@c.txt: not found in workspace]", appended);
            Assert.DoesNotContain("d.txt", appended);
            Assert.Contains("1 more @mention(s) skipped", appended);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Resolve_retries_without_trailing_punctuation_when_the_as_is_path_does_not_resolve()
    {
        var dir = NewScratchDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "hello\n");
            // A trailing COMMA can't ever come out of ExtractMentions (comma isn't in the path-ish character
            // class), but Resolve is exercised directly here to lock in the fallback itself: "README.md," doesn't
            // exist as-is, so it retries "README.md" (comma stripped) and finds it. (A trailing PERIOD isn't a
            // useful test of this fallback on Windows: the Win32 API itself silently trims a trailing '.' off a
            // path, so "README.md." already resolves directly — the retry branch never even fires for that case.)
            var resolved = CodeMentions.Resolve(dir, "README.md,");
            Assert.True(resolved.Found);
            Assert.Equal("README.md", resolved.Path);
            Assert.Contains("hello", resolved.Content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Resolve_rejects_a_path_that_escapes_the_workspace()
    {
        var dir = NewScratchDir();
        try
        {
            var resolved = CodeMentions.Resolve(dir, "../../windows/system32/config");
            Assert.False(resolved.Found);
        }
        finally { Directory.Delete(dir, true); }
    }
}
