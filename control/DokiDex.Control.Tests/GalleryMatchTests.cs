using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure library filter predicate (free-text prompt search + kind filter). No filesystem, no GPU.
public class GalleryMatchTests
{
    [Fact]
    public void Blank_filters_match_everything()
    {
        Assert.True(GalleryService.Match("a fox", "image", null, null));
        Assert.True(GalleryService.Match("a fox", "image", "   ", "  "));
    }

    [Fact]
    public void Query_matches_anywhere_in_the_prompt_case_insensitively()
    {
        Assert.True(GalleryService.Match("a neon FOX in rain", "image", "fox", null));
        Assert.False(GalleryService.Match("a neon fox", "image", "dragon", null));
    }

    [Fact]
    public void Kind_filter_must_equal_the_item_kind()
    {
        Assert.True(GalleryService.Match("x", "video", null, "video"));
        Assert.False(GalleryService.Match("x", "image", null, "video"));
        Assert.True(GalleryService.Match("x", "Image", null, "image"));   // case-insensitive
    }

    [Fact]
    public void Query_and_kind_are_anded()
    {
        Assert.True(GalleryService.Match("a fox", "image", "fox", "image"));
        Assert.False(GalleryService.Match("a fox", "image", "fox", "video"));   // right text, wrong kind
        Assert.False(GalleryService.Match("a cat", "image", "fox", "image"));   // right kind, wrong text
    }

    // ---- keyboard-triage curation view (PassesView) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("whatever")]   // unknown view => show everything
    public void Blank_or_unknown_view_shows_everything(string? view)
    {
        Assert.True(GalleryService.PassesView(false, false, view));
        Assert.True(GalleryService.PassesView(true, false, view));
        Assert.True(GalleryService.PassesView(false, true, view));
    }

    [Fact]
    public void Fav_view_shows_only_favorites_that_are_not_trashed()
    {
        Assert.True(GalleryService.PassesView(true, false, "fav"));
        Assert.False(GalleryService.PassesView(false, false, "fav"));
        Assert.False(GalleryService.PassesView(true, true, "fav"));   // a trashed favorite is still trash
    }

    [Fact]
    public void Trash_view_shows_only_trashed()
    {
        Assert.True(GalleryService.PassesView(false, true, "trash"));
        Assert.True(GalleryService.PassesView(true, true, "trash"));
        Assert.False(GalleryService.PassesView(true, false, "trash"));
    }

    [Fact]
    public void Active_view_hides_trash_keeps_the_rest()
    {
        Assert.True(GalleryService.PassesView(false, false, "active"));
        Assert.True(GalleryService.PassesView(true, false, "active"));
        Assert.False(GalleryService.PassesView(false, true, "active"));
    }

    [Fact]
    public void Untriaged_view_shows_only_unflagged()
    {
        Assert.True(GalleryService.PassesView(false, false, "untriaged"));
        Assert.False(GalleryService.PassesView(true, false, "untriaged"));
        Assert.False(GalleryService.PassesView(false, true, "untriaged"));
    }
}
