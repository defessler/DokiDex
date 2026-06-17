using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure, model-free color math: sRGB->LAB, nearest-palette recolor, dominant-color extraction, hex parsing.
public class ColorToolsTests
{
    [Fact]
    public void Black_and_white_map_to_the_expected_lab_lightness()
    {
        var (lb, _, _) = ColorTools.RgbToLab(0, 0, 0);
        var (lw, _, _) = ColorTools.RgbToLab(255, 255, 255);
        Assert.Equal(0, lb, 1);
        Assert.Equal(100, lw, 1);
    }

    [Fact]
    public void NearestIndex_picks_the_perceptually_closest_palette_color()
    {
        var pal = new List<(double, double, double)>
        {
            ColorTools.RgbToLab(255, 0, 0),   // 0 red
            ColorTools.RgbToLab(0, 0, 255),   // 1 blue
        };
        Assert.Equal(0, ColorTools.NearestIndex(new Rgb(250, 10, 10), pal));   // near red
        Assert.Equal(1, ColorTools.NearestIndex(new Rgb(10, 10, 240), pal));   // near blue
    }

    [Fact]
    public void RemapToPalette_snaps_each_pixel_and_preserves_alpha()
    {
        // two pixels: near-red (opaque), near-blue (alpha 200) -> snapped to exact palette colors
        var rgba = new byte[] { 250, 5, 5, 255, 5, 5, 250, 200 };
        var pal = new List<Rgb> { new(255, 0, 0), new(0, 0, 255) };
        var outp = ColorTools.RemapToPalette(rgba, pal);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 200 }, outp);
    }

    [Fact]
    public void RemapToPalette_with_empty_palette_returns_an_unchanged_copy()
    {
        var rgba = new byte[] { 1, 2, 3, 4 };
        var outp = ColorTools.RemapToPalette(rgba, new List<Rgb>());
        Assert.Equal(rgba, outp);
        Assert.NotSame(rgba, outp);   // non-destructive
    }

    [Fact]
    public void RemapToPalette_rejects_a_non_rgba_buffer()
        => Assert.Throws<System.ArgumentException>(() => ColorTools.RemapToPalette(new byte[] { 1, 2, 3 }, new List<Rgb> { new(0, 0, 0) }));

    [Fact]
    public void DominantColors_finds_the_two_populations()
    {
        // 3 red pixels, 2 blue pixels (all opaque)
        var rgba = new List<byte>();
        for (int i = 0; i < 3; i++) rgba.AddRange(new byte[] { 255, 0, 0, 255 });
        for (int i = 0; i < 2; i++) rgba.AddRange(new byte[] { 0, 0, 255, 255 });
        var dom = ColorTools.DominantColors(rgba.ToArray(), 2);
        Assert.Equal(2, dom.Count);
        Assert.Equal(new Rgb(255, 0, 0), dom[0]);   // most populous first
        Assert.Equal(new Rgb(0, 0, 255), dom[1]);
    }

    [Fact]
    public void DominantColors_ignores_transparent_pixels()
    {
        var rgba = new byte[] { 255, 0, 0, 10, 0, 255, 0, 255 };   // transparent red + opaque green
        var dom = ColorTools.DominantColors(rgba, 4);
        Assert.Single(dom);
        Assert.Equal(new Rgb(0, 255, 0), dom[0]);
    }

    [Theory]
    [InlineData("#ff8800", 255, 136, 0)]
    [InlineData("ff8800", 255, 136, 0)]
    [InlineData("#f80", 255, 136, 0)]
    public void ParseHex_accepts_3_and_6_digit_forms(string hex, byte r, byte g, byte b)
        => Assert.Equal(new Rgb(r, g, b), ColorTools.ParseHex(hex));

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("#12")]
    public void ParseHex_rejects_garbage(string hex) => Assert.Null(ColorTools.ParseHex(hex));

    [Fact]
    public void ToHex_round_trips() => Assert.Equal("#ff8800", ColorTools.ToHex(new Rgb(255, 136, 0)));

    [Fact]
    public void ParsePalette_parses_a_list_and_drops_bad_entries()
    {
        var pal = ColorTools.ParsePalette("#ff0000, 00ff00 , nope, #00f");
        Assert.Equal(3, pal.Count);
        Assert.Equal(new Rgb(255, 0, 0), pal[0]);
        Assert.Equal(new Rgb(0, 255, 0), pal[1]);
        Assert.Equal(new Rgb(0, 0, 255), pal[2]);
    }
}
