using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The streaming DISPLAY seam (1.1): as CodeAgent streams a model's content live, SEARCH/REPLACE block bodies must
// be suppressed (they render as a colored diff at the approval gate instead — showing the raw markers too would
// be noisy and duplicate the diff). StreamDisplayFilter buffers partial (not-yet-newline-terminated) lines so a
// marker split across two streamed chunks is still recognized as ONE line. Pure, total, no console/model — written
// FIRST (red) per the plan's TDD instruction.
public class StreamDisplayFilterTests
{
    [Fact]
    public void Prose_passes_through_unchanged()
    {
        var f = new StreamDisplayFilter();
        Assert.Equal("Hello, world!\n", f.Push("Hello, world!\n"));
        Assert.Equal("", f.Flush());
    }

    [Fact]
    public void A_search_replace_block_body_is_suppressed()
    {
        var f = new StreamDisplayFilter();
        var input = "path.cs\n<<<<<<< SEARCH\nold line\n=======\nnew line\n>>>>>>> REPLACE\n";
        // The path line (above the marker) is plain prose and passes; everything from "<<<<<<<" through
        // ">>>>>>>" inclusive is the block body and is suppressed.
        Assert.Equal("path.cs\n", f.Push(input));
    }

    [Fact]
    public void Prose_block_prose_only_shows_the_prose()
    {
        var f = new StreamDisplayFilter();
        var input = "Explaining the change:\npath.cs\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE\nDone.\n";
        Assert.Equal("Explaining the change:\npath.cs\nDone.\n", f.Push(input));
    }

    [Fact]
    public void A_marker_split_across_two_chunks_is_still_recognized()
    {
        var f = new StreamDisplayFilter();
        // "<<<<<<< SEARCH" arrives split mid-line across two Push calls — must still suppress correctly.
        var s1 = f.Push("some prose\n<<<<<<< SEA");
        Assert.Equal("some prose\n", s1);   // the partial marker line is buffered, not shown yet

        var s2 = f.Push("RCH\nold\n=======\nnew\n>>>>>>> REPLACE\nafter\n");
        Assert.Equal("after\n", s2);
    }

    [Fact]
    public void A_trailing_partial_line_with_no_newline_is_buffered_then_flushed()
    {
        var f = new StreamDisplayFilter();
        Assert.Equal("", f.Push("no newline yet"));
        Assert.Equal("no newline yet", f.Flush());
    }

    [Fact]
    public void Flush_of_an_unterminated_block_body_is_suppressed_not_leaked()
    {
        var f = new StreamDisplayFilter();
        // The stream ends mid-block (no closing marker ever arrives) — the partial edit body must never leak
        // to the display as stray text.
        f.Push("path.cs\n<<<<<<< SEARCH\nold");
        Assert.Equal("", f.Flush());
    }
}
