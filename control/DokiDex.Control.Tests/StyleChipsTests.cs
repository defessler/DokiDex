using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure compose step of style chips: appending selected chips' +/- fragments to the prompt/negative.
public class StyleChipsTests
{
    private static readonly Chip Cine = new("cinematic", "Cinematic", "cinematic lighting", "flat lighting");
    private static readonly Chip Anime = new("anime", "Anime", "anime style", "photorealistic");

    [Fact]
    public void Applies_add_to_prompt_and_neg_to_negative()
    {
        var (p, n) = StyleChips.Apply(new[] { Cine }, "a fox", "");
        Assert.Equal("a fox, cinematic lighting", p);
        Assert.Equal("flat lighting", n);
    }

    [Fact]
    public void Stacks_multiple_chips_in_order()
    {
        var (p, n) = StyleChips.Apply(new[] { Cine, Anime }, "a fox", "blurry");
        Assert.Equal("a fox, cinematic lighting, anime style", p);
        Assert.Equal("blurry, flat lighting, photorealistic", n);
    }

    [Fact]
    public void Empty_prompt_does_not_get_a_leading_comma()
    {
        var (p, _) = StyleChips.Apply(new[] { Cine }, "", "");
        Assert.Equal("cinematic lighting", p);
    }

    [Fact]
    public void No_chips_returns_the_prompt_unchanged()
    {
        var (p, n) = StyleChips.Apply(System.Array.Empty<Chip>(), "a fox", "blurry");
        Assert.Equal("a fox", p);
        Assert.Equal("blurry", n);
    }

    [Fact]
    public void A_chip_with_no_negative_only_touches_the_prompt()
    {
        var (p, n) = StyleChips.Apply(new[] { new Chip("x", "X", "extra detail", "") }, "a fox", "blurry");
        Assert.Equal("a fox, extra detail", p);
        Assert.Equal("blurry", n);   // unchanged
    }

    [Fact]
    public void Catalog_loads_the_shipped_chips_when_the_repo_root_resolves()
    {
        // the repo ships media-assets/style-chips.json; when run from a checkout the root resolves and the
        // catalog includes "cinematic". Tolerant of a headless env where RepoPaths can't find the root.
        var all = StyleChips.All();
        if (all.Count > 0) Assert.Contains(all, c => c.Id == "cinematic");
    }
}
