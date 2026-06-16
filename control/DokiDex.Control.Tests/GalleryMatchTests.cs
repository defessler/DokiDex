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
}
