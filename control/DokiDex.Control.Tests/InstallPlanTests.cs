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
}
