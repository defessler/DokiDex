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
    public void Upscaler_engine_is_emitted_only_with_a_post_pass_on_image_edit()
    {
        // needs -Upscale or -Refine (and image/edit) to matter; dropped otherwise
        var a = GenCli.BuildArgs(new GenRequest("x", "image", Upscale: true, Upscaler: "anime", OutPath: "o"));
        var i = a.IndexOf("-Upscaler"); Assert.True(i >= 0); Assert.Equal("anime", a[i + 1]);
        Assert.DoesNotContain("-Upscaler", GenCli.BuildArgs(new GenRequest("x", "image", Upscaler: "anime", OutPath: "o")));       // no post-pass
        Assert.DoesNotContain("-Upscaler", GenCli.BuildArgs(new GenRequest("x", "video", Upscale: true, Upscaler: "anime", OutPath: "o"))); // wrong kind
    }

    [Fact]
    public void ControlNet_units_serialize_to_one_json_arg_on_image_edit()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image",
            ControlNets: new[] { new ControlUnit("canny.safetensors", "c.png", 0.8, "canny") }, OutPath: "o"));
        var i = a.IndexOf("-ControlNets"); Assert.True(i >= 0);
        var json = a[i + 1];
        Assert.Contains("canny.safetensors", json); Assert.Contains("c.png", json); Assert.Contains("canny", json);
        // a unit with no model is dropped -> no arg; wrong kind -> no arg
        Assert.DoesNotContain("-ControlNets", GenCli.BuildArgs(new GenRequest("x", "image", ControlNets: new[] { new ControlUnit(null, "c.png") }, OutPath: "o")));
        Assert.DoesNotContain("-ControlNets", GenCli.BuildArgs(new GenRequest("x", "video", ControlNets: new[] { new ControlUnit("canny.safetensors") }, OutPath: "o")));
    }

    [Fact]
    public void ControlNet_stacks_up_to_three_units()
    {
        var four = new[] { new ControlUnit("a"), new ControlUnit("b"), new ControlUnit("c"), new ControlUnit("d") };
        var a = GenCli.BuildArgs(new GenRequest("x", "image", ControlNets: four, OutPath: "o"));
        var json = a[a.IndexOf("-ControlNets") + 1];
        Assert.Contains("\"a\"", json); Assert.Contains("\"c\"", json);
        Assert.DoesNotContain("\"d\"", json);   // capped at 3
    }

    [Fact]
    public void Frame_interpolation_is_emitted_only_for_video_kinds()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "video", Interpolate: "RIFE", InterpolateMult: 4, OutPath: "o"));
        var i = a.IndexOf("-Interpolate"); Assert.True(i >= 0); Assert.Equal("RIFE", a[i + 1]);
        Assert.Contains("-InterpolateMult", a);
        Assert.DoesNotContain("-Interpolate", GenCli.BuildArgs(new GenRequest("x", "image", Interpolate: "RIFE", OutPath: "o")));
    }

    [Fact]
    public void End_image_is_emitted_only_for_video_kinds()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "video", EndImage: "e.png", OutPath: "o"));
        var i = a.IndexOf("-EndImage"); Assert.True(i >= 0); Assert.Equal("e.png", a[i + 1]);
        Assert.Contains("-EndImage", GenCli.BuildArgs(new GenRequest("x", "i2v", EndImage: "e.png", OutPath: "o")));
        Assert.DoesNotContain("-EndImage", GenCli.BuildArgs(new GenRequest("x", "image", EndImage: "e.png", OutPath: "o")));   // not a video kind
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
    public void Reference_is_emitted_only_with_an_init_image_on_image_edit()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", InitImage: "ref.png", Reference: true, RefWeight: 0.7, OutPath: "o"));
        Assert.Contains("-Reference", a);
        var i = a.IndexOf("-RefWeight"); Assert.True(i >= 0); Assert.Equal("0.7", a[i + 1]);
        Assert.DoesNotContain("-Reference", GenCli.BuildArgs(new GenRequest("x", "image", Reference: true, OutPath: "o")));   // no init image
        Assert.DoesNotContain("-Reference", GenCli.BuildArgs(new GenRequest("x", "video", InitImage: "r.png", Reference: true, OutPath: "o"))); // wrong kind
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
    public void Music_lyrics_duration_bpm_pass_through_when_set()
    {
        var a = GenCli.BuildArgs(new GenRequest("lofi", "music", Lyrics: "la la la", Duration: 30, Bpm: 90, OutPath: "o"));
        var li = a.IndexOf("-Lyrics"); Assert.True(li >= 0); Assert.Equal("la la la", a[li + 1]);
        var di = a.IndexOf("-Duration"); Assert.True(di >= 0); Assert.Equal("30", a[di + 1]);
        var bi = a.IndexOf("-Bpm"); Assert.True(bi >= 0); Assert.Equal("90", a[bi + 1]);
    }

    [Fact]
    public void Music_knobs_are_omitted_when_unset()
    {
        var a = GenCli.BuildArgs(new GenRequest("lofi", "music", OutPath: "o"));
        Assert.DoesNotContain("-Lyrics", a);
        Assert.DoesNotContain("-Duration", a);
        Assert.DoesNotContain("-Bpm", a);
    }

    [Fact]
    public void Lora_passes_through_as_a_separate_value_arg_when_set()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", Lora: "anime:0.8,detail", OutPath: "o"));
        var i = a.IndexOf("-Lora");
        Assert.True(i >= 0);
        Assert.Equal("anime:0.8,detail", a[i + 1]);
    }

    [Fact]
    public void Lora_is_omitted_when_blank()
        => Assert.DoesNotContain("-Lora", GenCli.BuildArgs(new GenRequest("x", "image", Lora: "  ", OutPath: "o")));

    [Fact]
    public void Negative_passes_through_as_a_separate_value_arg_when_set()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", Negative: "blurry, extra fingers", OutPath: "o"));
        var i = a.IndexOf("-Negative");
        Assert.True(i >= 0);
        Assert.Equal("blurry, extra fingers", a[i + 1]);
    }

    [Fact]
    public void Negative_is_omitted_when_blank()
        => Assert.DoesNotContain("-Negative", GenCli.BuildArgs(new GenRequest("x", "image", Negative: "  ", OutPath: "o")));

    [Fact]
    public void Segment_passes_through_as_a_separate_value_arg_when_set()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", Segment: "face, hands:0.6", OutPath: "o"));
        var i = a.IndexOf("-Segment");
        Assert.True(i >= 0);
        Assert.Equal("face, hands:0.6", a[i + 1]);
    }

    [Fact]
    public void Workflow_passes_through_when_set()
    {
        var a = GenCli.BuildArgs(new GenRequest("x", "image", Workflow: "SUPIR", OutPath: "o"));
        var i = a.IndexOf("-Workflow"); Assert.True(i >= 0); Assert.Equal("SUPIR", a[i + 1]);
        Assert.DoesNotContain("-Workflow", GenCli.BuildArgs(new GenRequest("x", "image", OutPath: "o")));
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
