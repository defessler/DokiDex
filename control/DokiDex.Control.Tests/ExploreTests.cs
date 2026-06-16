using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure helpers behind Exploration Mode: the seed plan + the similarity ladder. GPU-free, locked here.
public class ExploreTests
{
    [Fact]
    public void Fixed_base_seed_yields_reproducible_neighbors()
        => Assert.Equal(new[] { 100, 101, 102, 103 }, Explore.Seeds(100, 4));

    [Fact]
    public void Negative_base_seed_yields_all_random()
        => Assert.Equal(new[] { -1, -1, -1 }, Explore.Seeds(-1, 3));

    [Fact]
    public void Count_is_clamped_to_a_sane_range()
    {
        Assert.Single(Explore.Seeds(0, 0));        // floor 1
        Assert.Equal(16, Explore.Seeds(0, 999).Count);   // ceiling 16
    }

    [Fact]
    public void Similarity_ladder_runs_low_to_high()
    {
        Assert.Equal(5, Explore.SimilarityLadder.Length);
        Assert.True(Explore.SimilarityLadder[0] < Explore.SimilarityLadder[^1]);
        Assert.All(Explore.SimilarityLadder, v => Assert.InRange(v, 0.0, 1.0));
    }
}
