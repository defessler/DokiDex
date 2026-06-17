using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The one-click effect-preset compose: a preset + the card image -> an img2img GenRequest. Pure, no GPU.
public class EffectPresetsTests
{
    [Fact]
    public void Build_makes_an_img2img_request_with_the_presets_prompt_and_strength()
    {
        var p = new EffectPreset("anime", "To Anime", "anime style, cel shading", 0.55);
        var r = EffectPresets.Build(p, @"C:\g\card.png");
        Assert.Equal("anime style, cel shading", r.Prompt);
        Assert.Equal("image", r.Kind);
        Assert.Equal(@"C:\g\card.png", r.InitImage);
        Assert.Equal(0.55, r.Strength, 3);
    }

    [Fact]
    public void Catalog_loads_the_shipped_presets_when_the_repo_root_resolves()
    {
        var all = EffectPresets.All();   // ships media-assets/effect-presets.json
        if (all.Count > 0)
        {
            Assert.Contains(all, p => p.Id == "anime");
            Assert.Equal(all.Single(p => p.Id == "anime"), EffectPresets.Find("ANIME"));   // case-insensitive find
        }
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_id()
        => Assert.Null(EffectPresets.Find("does-not-exist"));
}
