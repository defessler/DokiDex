using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure multi-speaker dialogue parser (named speakers, carried-forward lines, inline performance tags).
public class DialogueTests
{
    [Fact]
    public void Named_speakers_split_into_lines()
    {
        var d = Dialogue.Parse("HERO: Hello there.\nVILLAIN: We meet again.");
        Assert.Equal(2, d.Count);
        Assert.Equal("HERO", d[0].Speaker);
        Assert.Equal("Hello there.", d[0].Text);
        Assert.Equal("VILLAIN", d[1].Speaker);
    }

    [Fact]
    public void An_unlabeled_line_continues_the_current_speaker()
    {
        var d = Dialogue.Parse("HERO: First.\nStill the hero.");
        Assert.Equal(2, d.Count);
        Assert.Equal("HERO", d[1].Speaker);
        Assert.Equal("Still the hero.", d[1].Text);
    }

    [Fact]
    public void A_leading_unlabeled_line_is_the_narrator()
    {
        var d = Dialogue.Parse("The sun rises over the ridge.");
        Assert.Single(d);
        Assert.Equal("Narrator", d[0].Speaker);
    }

    [Fact]
    public void A_performance_tag_sets_delivery_and_is_stripped_from_the_text()
    {
        var d = Dialogue.Parse("HERO: [excited] We did it!");
        Assert.Single(d);
        Assert.Equal("We did it!", d[0].Text);          // tag removed from spoken text
        Assert.True(d[0].Exaggeration > 0.5);            // excited => stronger
    }

    [Fact]
    public void Whisper_tag_lowers_exaggeration()
    {
        var d = Dialogue.Parse("HERO: [whispers] keep it down");
        Assert.True(d[0].Exaggeration < 0.5);
        Assert.Equal("keep it down", d[0].Text);
    }

    [Fact]
    public void Unknown_tags_are_stripped_but_keep_default_delivery()
    {
        var d = Dialogue.Parse("HERO: [mumbling] something");
        Assert.Equal("something", d[0].Text);
        Assert.Equal(0.5, d[0].Exaggeration, 3);
    }

    [Fact]
    public void Blank_lines_are_skipped_and_empty_input_is_empty()
    {
        Assert.Empty(Dialogue.Parse("  "));
        Assert.Single(Dialogue.Parse("\n\nHERO: hi\n\n"));
    }

    [Fact]
    public void A_line_that_is_only_a_tag_is_dropped()
        => Assert.Empty(Dialogue.Parse("HERO: [laughs]"));   // nothing left to speak
}
