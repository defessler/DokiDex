using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The SEARCH/REPLACE applier — the edit crux. These lock the strategy ladder: exact-unique applies; ambiguous
// exact / no-match return reflection-friendly errors (so the agent can recover); whitespace-only drift (the most
// common weak-model failure) still matches. Pure — no disk, no model.
public class CodeEditTests
{
    [Fact]
    public void Exact_unique_match_is_applied()
    {
        var content = "line1\nfoo = 1\nline3\n";
        var r = CodeEdit.ApplyEdit(content, "foo = 1", "foo = 2");
        Assert.True(r.Ok);
        Assert.Equal("line1\nfoo = 2\nline3\n", r.NewContent);
        Assert.Equal("exact", r.Strategy);
    }

    [Fact]
    public void Multiple_exact_matches_are_rejected_as_ambiguous()
    {
        var content = "x = 1\nx = 1\n";
        var r = CodeEdit.ApplyEdit(content, "x = 1", "x = 2");
        Assert.False(r.Ok);
        Assert.Contains("multiple", r.Error, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, r.NewContent);   // unchanged on failure
    }

    [Fact]
    public void No_match_returns_a_reflection_friendly_error()
    {
        var r = CodeEdit.ApplyEdit("a\nb\nc\n", "nonexistent", "z");
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Equal("a\nb\nc\n", r.NewContent);
    }

    [Fact]
    public void Empty_search_is_rejected()
        => Assert.False(CodeEdit.ApplyEdit("abc", "", "z").Ok);

    [Fact]
    public void Whitespace_flexible_match_applies_when_only_indentation_differs()
    {
        // The model copied the line but with different leading indentation than the file — a very common weak-model
        // failure. The whitespace-flexible stage must still locate and replace the real line.
        var content = "class C {\n        int x = 1;\n}\n";   // 8-space indent in the file
        var search = "    int x = 1;";                          // model used 4-space indent
        var replace = "        int x = 2;";
        var r = CodeEdit.ApplyEdit(content, search, replace);
        Assert.True(r.Ok);
        Assert.Contains("int x = 2;", r.NewContent);
        Assert.DoesNotContain("int x = 1;", r.NewContent);
        Assert.Equal("whitespace", r.Strategy);
    }

    [Fact]
    public void A_multi_line_block_is_replaced_as_a_unit()
    {
        var content = "a\nb\nc\nd\n";
        var r = CodeEdit.ApplyEdit(content, "b\nc", "B\nC\nC2");
        Assert.True(r.Ok);
        Assert.Equal("a\nB\nC\nC2\nd\n", r.NewContent);
    }

    [Fact]
    public void CRLF_line_endings_are_preserved()
    {
        var content = "x\r\ny\r\nz\r\n";
        var r = CodeEdit.ApplyEdit(content, "y", "Y");
        Assert.True(r.Ok);
        Assert.Equal("x\r\nY\r\nz\r\n", r.NewContent);   // file's CRLF style restored on re-join
    }

    [Fact]
    public void A_partial_indent_search_does_not_corrupt_the_files_indentation()
    {
        // Regression: raw-substring matching would splice inside the 8-space indent and leave 12 spaces. Line-based
        // matching replaces the WHOLE line, so the replacement's own indentation is what lands.
        var content = "class C {\n        int x = 1;\n}\n";
        var r = CodeEdit.ApplyEdit(content, "    int x = 1;", "        int x = 2;");
        Assert.Equal("class C {\n        int x = 2;\n}\n", r.NewContent);
    }

    // ---- diff rendering (edit preview) ----

    [Fact]
    public void RenderDiff_shows_removed_and_added_lines_with_context()
    {
        var s = CodeEdit.RenderDiff("f.cs", "a\nb\nc\n", "a\nB\nc\n");
        Assert.Contains("- b", s);
        Assert.Contains("+ B", s);
        Assert.Contains("  a", s);   // unchanged context
        Assert.Contains("  c", s);
    }

    [Fact]
    public void RenderDiff_reports_no_changes_for_identical_text()
        => Assert.Equal("(no changes)", CodeEdit.RenderDiff("f", "x\n", "x\n"));

    [Fact]
    public void RenderDiff_pure_addition_has_only_plus_lines()
    {
        var s = CodeEdit.RenderDiff("f", "a\nc\n", "a\nb\nc\n");
        Assert.Contains("+ b", s);
        Assert.DoesNotContain("\n- ", "\n" + s);   // nothing removed
    }

    // ---- SEARCH/REPLACE block parsing (the text edit protocol) ----

    [Fact]
    public void ParseSearchReplaceBlocks_extracts_path_search_replace()
    {
        var content = "Here is the change:\n\nsrc/app.cs\n<<<<<<< SEARCH\nint x = 1;\n=======\nint x = 2;\n>>>>>>> REPLACE\n";
        var blocks = CodeEdit.ParseSearchReplaceBlocks(content);
        Assert.Single(blocks);
        Assert.Equal("src/app.cs", blocks[0].Path);
        Assert.Equal("int x = 1;", blocks[0].Search);
        Assert.Equal("int x = 2;", blocks[0].Replace);
    }

    [Fact]
    public void ParseSearchReplaceBlocks_handles_multiple_blocks()
    {
        var content = "a.cs\n<<<<<<< SEARCH\nA\n=======\nB\n>>>>>>> REPLACE\nb.cs\n<<<<<<< SEARCH\nC\n=======\nD\n>>>>>>> REPLACE\n";
        var blocks = CodeEdit.ParseSearchReplaceBlocks(content);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("a.cs", blocks[0].Path);
        Assert.Equal("b.cs", blocks[1].Path);
        Assert.Equal("D", blocks[1].Replace);
    }

    [Fact]
    public void ParseSearchReplaceBlocks_strips_code_fence_and_backticks_from_path()
    {
        var content = "```cs src/app.cs\n<<<<<<< SEARCH\nx\n=======\ny\n>>>>>>> REPLACE\n```";
        var blocks = CodeEdit.ParseSearchReplaceBlocks(content);
        Assert.Single(blocks);
        Assert.Equal("src/app.cs", blocks[0].Path);
    }

    [Fact]
    public void ParseSearchReplaceBlocks_does_not_split_on_equality_in_code()
    {
        // The divider must be a line of ONLY '='; a code line like "a == b" must NOT end the SEARCH section.
        var content = "f.cs\n<<<<<<< SEARCH\nif (a == b) {\n=======\nif (a != b) {\n>>>>>>> REPLACE\n";
        var blocks = CodeEdit.ParseSearchReplaceBlocks(content);
        Assert.Single(blocks);
        Assert.Equal("if (a == b) {", blocks[0].Search);
        Assert.Equal("if (a != b) {", blocks[0].Replace);
    }

    [Fact]
    public void ParseSearchReplaceBlocks_returns_empty_for_plain_text()
        => Assert.Empty(CodeEdit.ParseSearchReplaceBlocks("just a normal answer with no edits"));

    [Fact]
    public void ParseSearchReplaceBlocks_skips_a_malformed_block_without_a_divider()
        => Assert.Empty(CodeEdit.ParseSearchReplaceBlocks("f.cs\n<<<<<<< SEARCH\nx\ny\nz\n"));

    [Fact]
    public void StripSearchReplaceBlocks_leaves_only_the_prose()
    {
        var content = "I'll bump the version.\nsrc/app.cs\n<<<<<<< SEARCH\nv = 1\n=======\nv = 2\n>>>>>>> REPLACE\nDone.";
        var prose = CodeEdit.StripSearchReplaceBlocks(content);
        Assert.Contains("I'll bump the version.", prose);
        Assert.Contains("Done.", prose);
        Assert.DoesNotContain("SEARCH", prose);
        Assert.DoesNotContain("v = 1", prose);
        Assert.DoesNotContain("src/app.cs", prose);
    }

    [Fact]
    public void StripSearchReplaceBlocks_returns_plain_text_unchanged()
        => Assert.Equal("just prose", CodeEdit.StripSearchReplaceBlocks("just prose"));

    // ---- edit-failure recovery hint ----

    [Fact]
    public void ApplyEdit_not_found_error_quotes_the_actual_nearby_file_text()
    {
        // The SEARCH's first line exists in the file but the full block doesn't match — the error must quote the
        // real current lines there (with line numbers) so the model can correct its block.
        var content = "class C {\n    int x = 1;\n    int y = 2;\n}\n";
        var r = CodeEdit.ApplyEdit(content, "int x = 1;\n    int z = 99;", "int x = 5;");
        Assert.False(r.Ok);
        Assert.Contains("int x = 1;", r.Error);   // quoted the real current text
        Assert.Contains("2:", r.Error);            // with a line number
    }

    [Fact]
    public void ApplyEdit_not_found_falls_back_to_the_generic_hint_when_no_line_matches()
    {
        var r = CodeEdit.ApplyEdit("a\nb\nc\n", "totally\nabsent\nlines", "x");
        Assert.False(r.Ok);
        Assert.Contains("Re-read the file", r.Error);
    }
}
