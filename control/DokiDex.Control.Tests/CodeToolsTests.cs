using System.IO;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The SECURITY GATE for the workspace-scoped coding tools: a chat-model-supplied path must resolve UNDER the
// workspace root or be rejected (null). These lock the escape vectors (traversal, absolute) BEFORE any read tool
// ships, mirroring the GalleryService / IsGallerySafePath canonical-prefix discipline. Pure — no disk.
public class CodeToolsTests
{
    private static readonly string Root = Path.GetFullPath("doki-ws-test-root");

    [Fact]
    public void A_nested_relative_path_resolves_under_the_workspace_root()
    {
        var p = CodeTools.ResolveWorkspacePath(Root, "control/Web/Chat.cs");
        Assert.NotNull(p);
        Assert.StartsWith(Root, p!);
    }

    [Fact]
    public void A_traversal_path_that_escapes_the_root_is_rejected()
    {
        // The classic escape: enough "../" to climb out of the workspace and reach a system file.
        Assert.Null(CodeTools.ResolveWorkspacePath(Root, "../../../../Windows/System32/drivers/etc/hosts"));
    }

    [Fact]
    public void An_absolute_path_is_rejected()
    {
        Assert.Null(CodeTools.ResolveWorkspacePath(Root, @"C:\Windows\System32\drivers\etc\hosts"));
    }

    [Fact]
    public void A_dotdot_that_stays_inside_the_root_is_allowed()
    {
        // "../" only escapes when it LEAVES root; a path that normalizes back under root is legitimate.
        var p = CodeTools.ResolveWorkspacePath(Root, "control/Web/../Web/Chat.cs");
        Assert.NotNull(p);
        Assert.StartsWith(Root, p!);
    }

    // ---- read_file: arg parsing ----

    [Fact]
    public void ParseReadArgs_reads_path_offset_and_limit()
    {
        var (path, offset, limit) = CodeTools.ParseReadArgs("""{"path":"control/Web/Chat.cs","offset":10,"limit":50}""");
        Assert.Equal("control/Web/Chat.cs", path);
        Assert.Equal(10, offset);
        Assert.Equal(50, limit);
    }

    [Fact]
    public void ParseReadArgs_defaults_when_absent_or_blank()
    {
        var (path, offset, limit) = CodeTools.ParseReadArgs(null);
        Assert.Equal("", path);
        Assert.Equal(1, offset);
        Assert.Equal(CodeTools.ReadDefaultLimit, limit);
    }

    [Fact]
    public void ParseReadArgs_tolerates_string_typed_ints()
    {
        var (_, offset, limit) = CodeTools.ParseReadArgs("""{"path":"a.txt","offset":"5","limit":"20"}""");
        Assert.Equal(5, offset);
        Assert.Equal(20, limit);
    }

    [Fact]
    public void ParseReadArgs_floors_nonpositive_offset_and_limit()
    {
        var (_, offset, limit) = CodeTools.ParseReadArgs("""{"path":"a.txt","offset":0,"limit":-3}""");
        Assert.Equal(1, offset);
        Assert.Equal(1, limit);
    }

    // ---- read_file: window formatting ----

    private static readonly string[] Lines = { "alpha", "bravo", "charlie", "delta", "echo" };

    [Fact]
    public void FormatFileWindow_numbers_lines_one_based_and_shows_all_when_it_fits()
    {
        var s = CodeTools.FormatFileWindow("f.txt", Lines, 1, 1000, 16000);
        Assert.Contains("f.txt (lines 1-5 of 5):", s);
        Assert.Contains("1\talpha", s);
        Assert.Contains("5\techo", s);
        Assert.DoesNotContain("more line", s);   // nothing truncated
    }

    [Fact]
    public void FormatFileWindow_windows_by_offset_and_limit_and_notes_remaining()
    {
        var s = CodeTools.FormatFileWindow("f.txt", Lines, 2, 2, 16000);
        Assert.Contains("f.txt (lines 2-3 of 5):", s);
        Assert.Contains("2\tbravo", s);
        Assert.Contains("3\tcharlie", s);
        Assert.DoesNotContain("1\talpha", s);
        Assert.Contains("2 more line(s) — read again with offset=4", s);
    }

    [Fact]
    public void FormatFileWindow_reports_when_offset_is_past_the_end()
        => Assert.Contains("past the end", CodeTools.FormatFileWindow("f.txt", Lines, 99, 10, 16000));

    [Fact]
    public void FormatFileWindow_caps_at_max_chars_and_points_to_the_resume_offset()
    {
        // A tiny maxChars forces truncation after the header + a line or two; the note must name a resume offset.
        var s = CodeTools.FormatFileWindow("f.txt", Lines, 1, 1000, 40);
        Assert.Contains("truncated", s);
        Assert.Contains("read again with offset=", s);
    }

    // ---- grep: arg parsing ----

    [Fact]
    public void ParseGrepArgs_reads_pattern_path_and_glob()
    {
        var (pattern, path, glob) = CodeTools.ParseGrepArgs("""{"pattern":"class\\s+\\w+","path":"control/Web","glob":"*.cs"}""");
        Assert.Equal("class\\s+\\w+", pattern);
        Assert.Equal("control/Web", path);
        Assert.Equal("*.cs", glob);
    }

    [Fact]
    public void ParseGrepArgs_pattern_only_leaves_path_and_glob_null()
    {
        var (pattern, path, glob) = CodeTools.ParseGrepArgs("""{"pattern":"TODO"}""");
        Assert.Equal("TODO", pattern);
        Assert.Null(path);
        Assert.Null(glob);
    }

    [Fact]
    public void ParseGrepArgs_missing_pattern_is_empty()
        => Assert.Equal("", CodeTools.ParseGrepArgs("{}").pattern);

    // ---- grep: glob → regex ----

    [Theory]
    [InlineData("*.cs", "Chat.cs", true)]
    [InlineData("*.cs", "Chat.txt", false)]
    [InlineData("Chat*.cs", "ChatTools.cs", true)]
    [InlineData("Chat*.cs", "GalleryService.cs", false)]
    public void GlobToRegex_matches_filenames(string glob, string name, bool expected)
        => Assert.Equal(expected, CodeTools.GlobToRegex(glob).IsMatch(name));

    [Fact]
    public void GlobToRegex_blank_matches_anything()
        => Assert.Matches(CodeTools.GlobToRegex(""), "anything.xyz");

    // ---- grep: line matching + formatting ----

    [Fact]
    public void MatchLines_returns_one_based_hits_and_clips_long_lines()
    {
        var lines = new[] { "no match here", "has FOO token", new string('x', 500) + "FOO" };
        var rx = new System.Text.RegularExpressions.Regex("FOO");
        var hits = CodeTools.MatchLines(lines, rx, 240);
        Assert.Equal(2, hits.Count);
        Assert.Equal(2, hits[0].line);
        Assert.Contains("FOO", hits[0].text);
        Assert.Equal(3, hits[1].line);
        Assert.Contains("…", hits[1].text);          // long line clipped
        Assert.True(hits[1].text.Length <= 241);
    }

    [Fact]
    public void FormatGrepResults_renders_hits_and_the_no_match_line()
    {
        Assert.Contains("No matches for /zzz/", CodeTools.FormatGrepResults("zzz", System.Array.Empty<(string, int, string)>(), false));
        var hits = new (string, int, string)[] { ("control/Web/Chat.cs", 12, "class Chat") };
        var s = CodeTools.FormatGrepResults("class Chat", hits, false);
        Assert.Contains("1 match(es) for /class Chat/:", s);
        Assert.Contains("control/Web/Chat.cs:12: class Chat", s);
    }

    [Fact]
    public void FormatGrepResults_notes_truncation()
        => Assert.Contains("capped at", CodeTools.FormatGrepResults("x", new (string, int, string)[] { ("a.cs", 1, "x") }, true));
}
