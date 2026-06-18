using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure speed/quality tier -> llama-swap model-name resolver. No network, no GPU.
public class LlmTiersTests
{
    [Theory]
    [InlineData("fast")]
    [InlineData("Fast")]
    [InlineData(" draft ")]
    [InlineData("quick")]
    public void Fast_aliases_resolve_to_the_fast_model(string tier)
        => Assert.Equal(LlmTiers.Fast, LlmTiers.Resolve(tier));

    [Theory]
    [InlineData("quality")]
    [InlineData("QUALITY")]
    [InlineData("slow")]
    [InlineData("max")]
    [InlineData("best")]
    public void Quality_aliases_resolve_to_the_quality_model(string tier)
        => Assert.Equal(LlmTiers.Quality, LlmTiers.Resolve(tier));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void Unknown_or_empty_tier_resolves_to_null_keeping_the_loaded_default(string? tier)
        => Assert.Null(LlmTiers.Resolve(tier));

    [Fact]
    public void Tier_model_names_match_the_llama_swap_block_names()
    {
        // these strings must equal model names in serving/llama-swap.yaml
        Assert.Equal("coder-fast", LlmTiers.Fast);
        Assert.Equal("coder-big", LlmTiers.Quality);
        Assert.Equal("vision", LlmTiers.Vision);
    }
}
