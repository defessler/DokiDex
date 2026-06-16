using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

// Component choices -> setup.ps1 args + the rough disk estimate.
public class InstallPlanTests
{
    [Fact]
    public void SetupArgs_media_full_emits_media_and_models_full()
        => Assert.Equal(new[] { "-Media", "-Models", "full" },
                        InstallPlan.SetupArgs(new InstallChoice(Media: true, ModelsFull: true)));

    [Fact]
    public void SetupArgs_media_lean_emits_only_media()
        => Assert.Equal(new[] { "-Media" },
                        InstallPlan.SetupArgs(new InstallChoice(Media: true, ModelsFull: false)));

    [Fact]
    public void SetupArgs_without_media_drops_models_full_even_if_set()
        => Assert.Empty(InstallPlan.SetupArgs(new InstallChoice(Media: false, ModelsFull: true)));

    [Fact]
    public void SetupArgs_includes_tts_and_stt()
    {
        var a = InstallPlan.SetupArgs(new InstallChoice(Media: false, ModelsFull: false, Tts: true, Stt: true));
        Assert.Contains("-Tts", a);
        Assert.Contains("-Stt", a);
    }

    [Fact]
    public void EstimateGb_grows_with_components()
    {
        var lean = InstallPlan.EstimateGb(new InstallChoice(CoderModels: false, Media: false, ModelsFull: false));
        var full = InstallPlan.EstimateGb(new InstallChoice(CoderModels: true, Media: true, ModelsFull: true, Tts: true, Stt: true));
        Assert.True(full > lean);
        Assert.True(full >= 150);
    }

    [Fact]
    public void RequiredGb_is_estimate_plus_headroom()
    {
        var c = new InstallChoice(CoderModels: false, Media: false, ModelsFull: false);
        Assert.Equal(InstallPlan.EstimateGb(c) + InstallPlan.HeadroomGb, InstallPlan.RequiredGb(c));
    }

    [Fact]
    public void FitsFreeSpace_blocks_below_required_and_allows_at_or_above()
    {
        const long gb = 1_000_000_000L;
        var c = new InstallChoice(CoderModels: true, Media: true, ModelsFull: true);   // a large install
        long need = InstallPlan.RequiredGb(c);
        Assert.False(InstallPlan.FitsFreeSpace(c, (need - 1) * gb));   // one GB short -> blocked
        Assert.True(InstallPlan.FitsFreeSpace(c, need * gb));          // exactly enough -> allowed
        Assert.True(InstallPlan.FitsFreeSpace(c, (need + 50) * gb));   // ample -> allowed

        var tiny = new InstallChoice(CoderModels: false, Media: false, ModelsFull: false);  // ~core only
        Assert.True(InstallPlan.FitsFreeSpace(tiny, 20 * gb));         // fits on a modest drive
    }
}
