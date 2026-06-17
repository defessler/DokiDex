using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure series compiler: shared style merged into each cell's own prompt, shared aspect/negative/seed-per-cell.
public class ImageSetTests
{
    private static SeriesSpec Spec(string? style, params SeriesCell[] cells)
        => new(style, "1:1", null, new List<SeriesCell>(cells));

    [Fact]
    public void Each_cell_becomes_a_request_with_the_style_merged_into_its_prompt()
    {
        var reqs = ImageSet.Compile(Spec("watercolor, soft light", new SeriesCell("a fox", 7), new SeriesCell("a cat", 8)));
        Assert.Equal(2, reqs.Count);
        Assert.Equal("a fox, watercolor, soft light", reqs[0].Prompt);
        Assert.Equal("a cat, watercolor, soft light", reqs[1].Prompt);
        Assert.Equal("image", reqs[0].Kind);
    }

    [Fact]
    public void Each_cell_keeps_its_own_seed_so_one_can_be_rerolled_alone()
    {
        var reqs = ImageSet.Compile(Spec("style", new SeriesCell("a", 11), new SeriesCell("b", -1)));
        Assert.Equal(11, reqs[0].Seed);
        Assert.Equal(-1, reqs[1].Seed);   // this cell is free to reroll
    }

    [Fact]
    public void Shared_aspect_applies_to_all_cells()
    {
        var reqs = ImageSet.Compile(Spec("s", new SeriesCell("a"), new SeriesCell("b")));
        Assert.All(reqs, r => Assert.Equal("1:1", r.Aspect));
    }

    [Fact]
    public void A_cell_with_no_prompt_falls_back_to_the_style_alone()
    {
        var reqs = ImageSet.Compile(Spec("just the style", new SeriesCell("")));
        Assert.Single(reqs);
        Assert.Equal("just the style", reqs[0].Prompt);
    }

    [Fact]
    public void Wholly_empty_cells_are_skipped()
    {
        var reqs = ImageSet.Compile(new SeriesSpec("", null, null, new List<SeriesCell> { new(""), new("  ") }));
        Assert.Empty(reqs);
    }

    [Fact]
    public void No_style_keeps_the_bare_prompt()
    {
        var reqs = ImageSet.Compile(new SeriesSpec(null, null, null, new List<SeriesCell> { new("lone prompt") }));
        Assert.Equal("lone prompt", reqs[0].Prompt);
    }

    [Fact]
    public void Fast_defaults_on_for_an_exploration_grid()
        => Assert.True(ImageSet.Compile(Spec("s", new SeriesCell("a")))[0].Fast);
}
