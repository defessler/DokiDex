using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

// The GPU-free heart of DokiGen: the exact `doki.ps1 gen …` argv the panel shells. Locking this means the
// live run (which needs the card) is the only unverified surface. Mirrors serving/doki-gen.ps1's contract.
public class GenCliTests
{
    [Fact]
    public void Image_is_the_default_kind_no_switch_plus_out_and_noopen()
    {
        var a = GenCli.BuildArgs(new GenRequest("a koi", "image", OutPath: @"C:\t\x.png"));
        Assert.Equal(new[] { "gen", "a koi", "-Out", @"C:\t\x.png", "-NoOpen" }, a);
    }

    [Theory]
    [InlineData("video", "-Video")]
    [InlineData("music", "-Music")]
    [InlineData("edit", "-Edit")]
    [InlineData("i2v", "-I2v")]
    [InlineData("foley", "-Foley")]
    public void Each_kind_maps_to_its_switch(string kind, string sw)
    {
        var a = GenCli.BuildArgs(new GenRequest("x", kind, InitImage: "s.png", OutPath: "o"));
        Assert.Contains(sw, a);
        // exactly one kind switch is ever emitted
        Assert.Single(a, t => t is "-Video" or "-Music" or "-Edit" or "-I2v" or "-Foley");
    }

    [Fact]
    public void Prompt_is_positional_and_carried_verbatim()
    {
        // a prompt with spaces/quotes stays a single argv element (ProcessStartInfo.ArgumentList re-quotes it)
        var a = GenCli.BuildArgs(new GenRequest("neon \"koi\" dragon, rain", "image", OutPath: "o"));
        Assert.Equal("gen", a[0]);
        Assert.Equal("neon \"koi\" dragon, rain", a[1]);
    }

    [Fact]
    public void Fast_and_raw_pass_through()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "video", Fast: true, Raw: true, OutPath: "o"));
        Assert.Contains("-Fast", a);
        Assert.Contains("-Raw", a);
    }

    [Fact]
    public void Face_and_realism_pass_through_when_set()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", Face: true, Realism: true, OutPath: "o"));
        Assert.Contains("-Face", a);
        Assert.Contains("-Realism", a);
    }

    [Fact]
    public void Face_and_realism_are_off_by_default()
    {
        // both flags are opt-in: a plain request must never emit them (no behavior change)
        var a = GenCli.BuildArgs(new GenRequest("x", "image", OutPath: "o"));
        Assert.DoesNotContain("-Face", a);
        Assert.DoesNotContain("-Realism", a);
    }

    [Fact]
    public void Upscale_is_emitted_only_for_image_and_edit()
    {
        Assert.Contains("-Upscale", GenCli.BuildArgs(new GenRequest("x", "image", Upscale: true, OutPath: "o")));
        Assert.Contains("-Upscale", GenCli.BuildArgs(new GenRequest("x", "edit", Upscale: true, InitImage: "s", OutPath: "o")));
        // doki-gen.ps1 throws on -Upscale for these kinds, so the builder must NOT send it (no doomed command)
        Assert.DoesNotContain("-Upscale", GenCli.BuildArgs(new GenRequest("x", "video", Upscale: true, OutPath: "o")));
        Assert.DoesNotContain("-Upscale", GenCli.BuildArgs(new GenRequest("x", "music", Upscale: true, OutPath: "o")));
    }

    [Fact]
    public void Init_image_is_passed_as_a_separate_value_arg()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "edit", InitImage: @"C:\pics\in.png", OutPath: "o"));
        var i = a.IndexOf("-InitImage");
        Assert.True(i >= 0);
        Assert.Equal(@"C:\pics\in.png", a[i + 1]);   // value follows the flag as its own element
    }

    [Fact]
    public void Blank_init_image_and_out_are_omitted()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", InitImage: "  ", OutPath: ""));
        Assert.DoesNotContain("-InitImage", a);
        Assert.DoesNotContain("-Out", a);
        Assert.Equal(new[] { "gen", "x", "-NoOpen" }, a);   // -NoOpen is always present
    }

    [Theory]
    [InlineData("image", ".png")]
    [InlineData("edit", ".png")]
    [InlineData("video", ".mp4")]
    [InlineData("i2v", ".mp4")]
    [InlineData("foley", ".mp4")]
    [InlineData("music", ".mp3")]
    public void Out_extension_matches_the_kinds_primary_artifact(string kind, string ext)
        => Assert.Equal(ext, GenRequest.OutExtensionFor(kind));

    [Fact]
    public void Aspect_is_passed_as_a_separate_value_arg_when_set()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", Aspect: "16:9", OutPath: "o"));
        var i = a.IndexOf("-Aspect");
        Assert.True(i >= 0);
        Assert.Equal("16:9", a[i + 1]);   // value follows the flag as its own element
    }

    [Fact]
    public void Aspect_is_omitted_when_blank_or_null()
    {
        Assert.DoesNotContain("-Aspect", GenCli.BuildArgs(new GenRequest("x", "image", OutPath: "o")));
        Assert.DoesNotContain("-Aspect", GenCli.BuildArgs(new GenRequest("x", "image", Aspect: "  ", OutPath: "o")));
    }

    [Fact]
    public void Inline_preview_is_image_kinds_only()
    {
        Assert.True(GenRequest.IsInlineImageKind("image"));
        Assert.True(GenRequest.IsInlineImageKind("edit"));
        Assert.False(GenRequest.IsInlineImageKind("video"));
        Assert.False(GenRequest.IsInlineImageKind("music"));
    }

    [Fact]
    public void Edit_requires_an_init_image_other_kinds_do_not()
    {
        Assert.True(GenRequest.RequiresInitImage("edit"));
        Assert.False(GenRequest.RequiresInitImage("i2v"));   // i2v can synth a fresh frame
        Assert.False(GenRequest.RequiresInitImage("image"));
    }
}
