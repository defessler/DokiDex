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
}
