using System.Linq;
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
    [InlineData("reasoning")]
    [InlineData("REASONING")]
    [InlineData("Reasoning")]
    public void Reasoning_resolves_to_the_gpt_oss_20b_model(string tier)
        => Assert.Equal(LlmTiers.Reasoning, LlmTiers.Resolve(tier));

    [Fact]
    public void Resolve_reasoning_returns_the_correct_model_name()
        => Assert.Equal("fast-candidate-gptoss20b", LlmTiers.Resolve("reasoning"));

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
        Assert.Equal("fast-candidate-gptoss20b", LlmTiers.Reasoning);
    }

    [Fact]
    public void Available_returns_fast_and_reasoning_when_their_models_are_configured()
    {
        // Only coder-fast and fast-candidate-gptoss20b are configured; quality is absent
        var tiers = LlmTiers.Available(new[] { "coder-fast", "fast-candidate-gptoss20b" });
        var ids = tiers.Select(t => t.Id).ToList();
        Assert.Contains("fast", ids);
        Assert.Contains("reasoning", ids);
        Assert.DoesNotContain("quality", ids);
    }

    [Fact]
    public void Available_returns_empty_when_no_configured_models_match()
    {
        var tiers = LlmTiers.Available(new[] { "some-other-model", "unknown-model" });
        Assert.Empty(tiers);
    }

    [Fact]
    public void Available_returns_all_three_text_tiers_when_all_models_configured()
    {
        var tiers = LlmTiers.Available(new[] { LlmTiers.Fast, LlmTiers.Quality, LlmTiers.Reasoning });
        var ids = tiers.Select(t => t.Id).ToList();
        Assert.Contains("fast", ids);
        Assert.Contains("quality", ids);
        Assert.Contains("reasoning", ids);
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void Available_does_not_include_vision_as_a_speed_picker_option()
    {
        // vision is auto-applied on image attach, not a speed picker; must not appear in Available()
        var tiers = LlmTiers.Available(new[] { LlmTiers.Fast, LlmTiers.Quality, LlmTiers.Reasoning, LlmTiers.Vision });
        var ids = tiers.Select(t => t.Id).ToList();
        Assert.DoesNotContain("vision", ids);
    }

    [Fact]
    public void Available_is_case_insensitive_for_model_names()
    {
        // model ids from llama-swap /v1/models are expected lowercase but guard against case variations
        var tiers = LlmTiers.Available(new[] { "CODER-FAST", "coder-big" });
        var ids = tiers.Select(t => t.Id).ToList();
        Assert.Contains("fast", ids);
        Assert.Contains("quality", ids);
    }

    [Fact]
    public void Available_carries_labels_for_each_returned_tier()
    {
        var tiers = LlmTiers.Available(new[] { LlmTiers.Fast });
        Assert.Single(tiers);
        Assert.Equal("fast", tiers[0].Id);
        Assert.False(string.IsNullOrWhiteSpace(tiers[0].Label));
    }

    // ---- AllRoles (2.4): the full role table incl. vision, used by GET /api/llm/tiers ----

    [Fact]
    public void AllRoles_includes_vision_unlike_Available()
    {
        var ids = LlmTiers.AllRoles.Select(r => r.Id).ToList();
        Assert.Contains("vision", ids);
        Assert.Equal(4, ids.Count);
    }

    [Fact]
    public void AllRoles_carries_the_same_model_names_as_the_constants()
    {
        var byId = LlmTiers.AllRoles.ToDictionary(r => r.Id, r => r.Model);
        Assert.Equal(LlmTiers.Fast, byId["fast"]);
        Assert.Equal(LlmTiers.Quality, byId["quality"]);
        Assert.Equal(LlmTiers.Reasoning, byId["reasoning"]);
        Assert.Equal(LlmTiers.Vision, byId["vision"]);
    }

    // ---- IsWarmable (2.4): POST /api/llm/warm's validation seam -- reject arbitrary strings ----

    [Theory]
    [InlineData("coder-fast")]
    [InlineData("coder-big")]
    [InlineData("fast-candidate-gptoss20b")]
    [InlineData("vision")]
    [InlineData("CODER-FAST")]
    public void IsWarmable_accepts_known_tier_models_case_insensitively(string model)
        => Assert.True(LlmTiers.IsWarmable(model, Array.Empty<string?>()));

    [Fact]
    public void IsWarmable_accepts_a_catalog_llamaSwapModel_not_in_the_tier_table()
        => Assert.True(LlmTiers.IsWarmable("coder-candidate-a3b", new[] { "coder-candidate-a3b" }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rm -rf /")]
    [InlineData("some-random-string")]
    public void IsWarmable_rejects_unknown_or_empty_model_names(string? model)
        => Assert.False(LlmTiers.IsWarmable(model, new[] { "coder-candidate-a3b" }));

    [Fact]
    public void IsWarmable_ignores_null_or_blank_entries_in_the_catalog_list()
        => Assert.False(LlmTiers.IsWarmable("not-a-real-model", new[] { null, "", "   " }));
}
