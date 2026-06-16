using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Per-card refine-from-result: action -> the img2img GenRequest (init image + one recipe flag). Pure, no GPU.
public class RefineTests
{
    [Fact]
    public void Face_action_is_light_img2img_plus_the_face_segment_pass()
    {
        var r = Refine.Build("a knight", @"C:\g\x.png", "face");
        Assert.NotNull(r);
        Assert.Equal(@"C:\g\x.png", r!.InitImage);
        Assert.True(r.Face);
        Assert.Equal(0.35, r.Strength, 3);
    }

    [Fact]
    public void Hires_action_uses_the_refiner_at_moderate_creativity()
    {
        var r = Refine.Build("a knight", "x.png", "hires")!;
        Assert.True(r.Refine);
        Assert.Equal(0.40, r.Strength, 3);
    }

    [Fact]
    public void Upscale_action_is_a_pure_enlarge_at_zero_creativity()
    {
        var r = Refine.Build("a knight", "x.png", "upscale")!;
        Assert.True(r.Upscale);
        Assert.Equal(0.0, r.Strength, 3);
    }

    [Fact]
    public void The_card_image_becomes_the_init_image_and_prompt_carries_over()
    {
        var r = Refine.Build("neon fox", "art.png", "hires")!;
        Assert.Equal("neon fox", r.Prompt);
        Assert.Equal("art.png", r.InitImage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("teleport")]
    [InlineData(null)]
    public void Unknown_action_yields_null(string? action)
        => Assert.Null(Refine.Build("x", "y.png", action));
}
