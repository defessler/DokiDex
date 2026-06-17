using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure prompt-aware checkpoint router (keyword classify + pick). No GPU, no LLM.
public class ModelRouterTests
{
    private static readonly List<RoutableModel> Bases = new()
    {
        new("z-image-base", "z_image_bf16.safetensors", "Z-Image Base", true),     // versatile + default
        new("chroma-hd", "Chroma1-HD.safetensors", "Chroma HD", false),            // classifies as illustration
    };

    [Theory]
    [InlineData("a photo of a fox, 85mm bokeh", ImgClass.Photo)]
    [InlineData("an anime girl, cel shading", ImgClass.Illustration)]
    [InlineData("a logo with the word ACME", ImgClass.Text)]
    [InlineData("a fox", ImgClass.Versatile)]
    public void WantedClass_reads_the_prompt(string prompt, ImgClass expected)
        => Assert.Equal(expected, ModelRouter.WantedClass(prompt));

    [Theory]
    [InlineData("Chroma HD", ImgClass.Illustration)]
    [InlineData("RealVisXL Photo", ImgClass.Photo)]
    [InlineData("Ideogram-text", ImgClass.Text)]
    [InlineData("Z-Image Base", ImgClass.Versatile)]
    public void ClassifyModel_reads_the_name(string name, ImgClass expected)
        => Assert.Equal(expected, ModelRouter.ClassifyModel(name));

    [Fact]
    public void Pick_routes_an_illustration_prompt_to_the_illustration_model()
    {
        var m = ModelRouter.Pick("anime portrait, cel shading", Bases);
        Assert.Equal("chroma-hd", m!.Id);
    }

    [Fact]
    public void Pick_falls_back_to_the_default_when_no_class_matches()
    {
        var m = ModelRouter.Pick("a photo of a fox", Bases);   // no Photo model installed
        Assert.Equal("z-image-base", m!.Id);                   // -> the default
    }

    [Fact]
    public void Pick_returns_null_when_nothing_is_installed()
        => Assert.Null(ModelRouter.Pick("anything", new List<RoutableModel>()));

    [Fact]
    public void Pick_with_one_base_always_returns_it()
    {
        var one = new List<RoutableModel> { new("only", "only.safetensors", "Only Base", false) };
        Assert.Equal("only", ModelRouter.Pick("an anime photo logo", one)!.Id);
    }
}
