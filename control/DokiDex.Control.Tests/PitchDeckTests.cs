using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure halves of the pitch-deck export: LLM-prose parsing + self-contained HTML assembly. No GPU, no LLM.
public class PitchDeckTests
{
    [Fact]
    public void ParseProse_reads_labelled_logline_and_synopsis()
    {
        var (log, syn) = PitchDeck.ParseProse("LOGLINE: A fox seeks the sun.\nSYNOPSIS: In a frozen world, a lone fox journeys north.");
        Assert.Equal("A fox seeks the sun.", log);
        Assert.Equal("In a frozen world, a lone fox journeys north.", syn);
    }

    [Fact]
    public void ParseProse_falls_back_to_first_line_logline_rest_synopsis()
    {
        var (log, syn) = PitchDeck.ParseProse("A fox seeks the sun.\nThe rest is the story body.");
        Assert.Equal("A fox seeks the sun.", log);
        Assert.Equal("The rest is the story body.", syn);
    }

    [Fact]
    public void ParseProse_empty_is_empty()
    {
        var (log, syn) = PitchDeck.ParseProse("   ");
        Assert.Equal("", log);
        Assert.Equal("", syn);
    }

    [Fact]
    public void ParseProse_drops_a_logline_label_echoed_inside_the_synopsis()
    {
        var (_, syn) = PitchDeck.ParseProse("SYNOPSIS: the body here\nLOGLINE: a stray label");
        Assert.Equal("the body here", syn);
    }

    [Fact]
    public void BuildHtml_is_self_contained_and_includes_the_content()
    {
        var deck = new Deck("My Saga", "A fox seeks the sun.", "A long cold journey north.",
            new List<DeckScene> { new("a neon fox", "data:image/png;base64,AAAA", "image") },
            new List<DeckCast> { new("Hero", "the brave fox") });
        var html = PitchDeck.BuildHtml(deck);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("My Saga", html);
        Assert.Contains("A fox seeks the sun.", html);
        Assert.Contains("A long cold journey north.", html);
        Assert.Contains("data:image/png;base64,AAAA", html);   // image inlined => portable offline
        Assert.Contains("Hero", html);
        Assert.DoesNotContain("http://", html);                 // no external asset references
    }

    [Fact]
    public void BuildHtml_escapes_html_in_user_text()
    {
        var deck = new Deck("<x>", "", "", new List<DeckScene> { new("<script>bad", "data:,", "image") }, new List<DeckCast>());
        var html = PitchDeck.BuildHtml(deck);
        Assert.DoesNotContain("<script>bad", html);
        Assert.Contains("&lt;script&gt;bad", html);
    }

    [Fact]
    public void BuildHtml_omits_empty_logline_and_synopsis_sections()
    {
        var deck = new Deck("T", "", "", new List<DeckScene>(), new List<DeckCast>());
        var html = PitchDeck.BuildHtml(deck);
        Assert.DoesNotContain("Synopsis</h2>", html);
        Assert.Contains("Scenes (0)", html);
    }
}
