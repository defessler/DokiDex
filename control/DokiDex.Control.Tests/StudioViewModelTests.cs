using DokiDex.Control.Services;
using DokiDex.Control.ViewModels;
using Xunit;

namespace DokiDex.Control.Tests;

// Phase-1 state machine for the DokiGen Studio page. (Generate/Remix arg-building is Phase 2 and
// gets its own tests then; here we lock the gating + result-state transitions the UI binds to.)
public class StudioViewModelTests
{
    private static StudioViewModel New() => new(new DokiService());

    [Fact]
    public void Kinds_match_doki_gen_recipes()
    {
        // the picker must mirror serving/doki-gen.ps1 Resolve-GenKind 1:1, in order
        Assert.Equal(new[] { "image", "video", "music", "edit", "i2v", "foley" }, New().Kinds);
    }

    [Fact]
    public void Cannot_generate_until_media_mode_is_active()
    {
        var vm = New();
        Assert.False(vm.MediaActive);
        Assert.False(vm.CanGenerate);          // the GPU isn't in media mode -> guarded

        vm.MediaActive = true;
        Assert.True(vm.CanGenerate);           // now eligible
    }

    [Fact]
    public void Cannot_generate_while_a_generation_is_running()
    {
        var vm = New();
        vm.MediaActive = true;
        vm.IsGenerating = true;
        Assert.False(vm.CanGenerate);          // no double-fire mid-run

        vm.IsGenerating = false;
        Assert.True(vm.CanGenerate);
    }

    [Fact]
    public void CanGenerate_raises_change_notification_for_its_inputs()
    {
        var vm = New();
        int hits = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CanGenerate)) hits++; };
        vm.MediaActive = true;     // [NotifyPropertyChangedFor(CanGenerate)]
        vm.IsGenerating = true;    // [NotifyPropertyChangedFor(CanGenerate)]
        Assert.Equal(2, hits);     // the Generate button's IsEnabled re-evaluates on both
    }

    [Fact]
    public void HasResult_tracks_result_path()
    {
        var vm = New();
        Assert.False(vm.HasResult);            // empty-state: shows the inviting canvas
        vm.ResultPath = "C:/out/koi.png";
        Assert.True(vm.HasResult);             // result-state: shows the framed preview card
        vm.ResultPath = "";
        Assert.False(vm.HasResult);
    }

    [Fact]
    public void Design_sample_populates_the_happy_path()
    {
        var vm = New();
        vm.LoadDesignSample();
        Assert.True(vm.MediaActive);                       // guard hidden
        Assert.True(vm.HasResult);                         // preview card visible
        Assert.False(vm.IsGenerating);
        Assert.True(vm.CanGenerate);                       // ready to remix/regenerate
        Assert.False(string.IsNullOrWhiteSpace(vm.PromptText));
        Assert.Equal("image", vm.SelectedKind);
    }

    [Theory]
    [InlineData("empty", true, false, false)]
    [InlineData("generating", true, true, false)]
    [InlineData("guard", false, false, false)]
    [InlineData("result", true, false, true)]
    public void Design_variants_set_the_state_each_snapshot_captures(string variant, bool media, bool generating, bool hasResult)
    {
        var vm = New();
        vm.LoadDesignSample(variant);
        Assert.Equal(media, vm.MediaActive);
        Assert.Equal(generating, vm.IsGenerating);
        Assert.Equal(hasResult, vm.HasResult);
    }

    [Fact]
    public void PromptIsEmpty_tracks_the_prompt_for_the_placeholder()
    {
        var vm = New();
        Assert.True(vm.PromptIsEmpty);          // fresh -> placeholder shows
        vm.PromptText = "a koi";
        Assert.False(vm.PromptIsEmpty);
        vm.PromptText = "   ";                   // whitespace-only still counts as empty
        Assert.True(vm.PromptIsEmpty);
    }

    [Fact]
    public void ShowInitImage_only_for_edit_and_i2v()
    {
        var vm = New();
        vm.SelectedKind = "image"; Assert.False(vm.ShowInitImage);
        vm.SelectedKind = "edit";  Assert.True(vm.ShowInitImage);
        vm.SelectedKind = "i2v";   Assert.True(vm.ShowInitImage);
        vm.SelectedKind = "video"; Assert.False(vm.ShowInitImage);
    }

    [Fact]
    public void SwitchToMedia_invokes_the_host_callback()
    {
        var vm = New();
        int n = 0;
        vm.SwitchToMediaRequested = () => n++;
        vm.SwitchToMediaCommand.Execute(null);
        Assert.Equal(1, n);
    }

    [Fact]
    public void SwitchToMedia_is_safe_with_no_host_callback()
        => New().SwitchToMediaCommand.Execute(null);   // null callback (standalone) must not throw

    [Fact]
    public void ShowEmpty_only_when_idle_with_no_result()
    {
        var vm = New();
        vm.LoadDesignSample("empty");      Assert.True(vm.ShowEmpty);
        vm.LoadDesignSample("generating"); Assert.False(vm.ShowEmpty);   // the overlay owns the canvas
        vm.LoadDesignSample("result");     Assert.False(vm.ShowEmpty);   // the preview owns the canvas
    }
}
