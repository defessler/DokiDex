using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure camera->prompt-token compiler. No GPU, no motion node — just deterministic phrasing, locked here.
public class CameraTests
{
    [Fact]
    public void No_input_yields_empty()
        => Assert.Equal("", Camera.Phrase(new CameraSpec(null)));

    [Fact]
    public void Known_preset_maps_to_its_phrase()
    {
        Assert.Equal("dolly in", Camera.Phrase(new CameraSpec("dolly-in")));
        Assert.Equal("orbiting camera", Camera.Phrase(new CameraSpec("orbit")));
    }

    [Fact]
    public void Unknown_preset_is_ignored()
        => Assert.Equal("", Camera.Phrase(new CameraSpec("teleport")));

    [Fact]
    public void Signed_axes_map_to_direction()
    {
        Assert.Contains("pan left", Camera.Phrase(new CameraSpec(null, Pan: -5)));
        Assert.Contains("pan right", Camera.Phrase(new CameraSpec(null, Pan: 5)));
        Assert.Contains("zoom in", Camera.Phrase(new CameraSpec(null, Zoom: 5)));
        Assert.Contains("zoom out", Camera.Phrase(new CameraSpec(null, Zoom: -5)));
        Assert.Contains("tilt up", Camera.Phrase(new CameraSpec(null, Tilt: 4)));
    }

    [Fact]
    public void Magnitude_sets_intensity()
    {
        Assert.Equal("slow pan left", Camera.Phrase(new CameraSpec(null, Pan: -2)));   // |2| <= 3
        Assert.Equal("pan left", Camera.Phrase(new CameraSpec(null, Pan: -5)));        // mid = plain verb
        Assert.Equal("fast pan left", Camera.Phrase(new CameraSpec(null, Pan: -9)));   // |9| > 7
    }

    [Fact]
    public void Zero_axes_contribute_nothing()
        => Assert.Equal("zoom in", Camera.Phrase(new CameraSpec(null, Pan: 0, Tilt: 0, Zoom: 5, Roll: 0)));

    [Fact]
    public void Preset_and_axes_combine_in_order()
        => Assert.Equal("dolly in, pan right, zoom in",
            Camera.Phrase(new CameraSpec("dolly-in", Pan: 5, Zoom: 5)));
}
