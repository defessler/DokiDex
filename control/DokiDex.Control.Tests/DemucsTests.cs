using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure command-builder for Demucs stem separation. The tool isn't present in a dev env, so SeparateAsync
// degrades to "not installed" (verified at the endpoint); the argv construction is locked here.
public class DemucsTests
{
    [Fact]
    public void Builds_python_m_demucs_with_model_outdir_and_track_last()
    {
        var a = Demucs.BuildArgs(@"C:\g\song.mp3", @"C:\g\stems", "htdemucs");
        Assert.Equal("-m", a[0]); Assert.Equal("demucs", a[1]);
        Assert.Equal("-n", a[2]); Assert.Equal("htdemucs", a[3]);
        var oi = a.ToList().IndexOf("-o"); Assert.True(oi >= 0); Assert.Equal(@"C:\g\stems", a[oi + 1]);
        Assert.Equal(@"C:\g\song.mp3", a[^1]);   // the track path is the final arg
    }

    [Fact]
    public void Six_stem_model_is_honored()
        => Assert.Contains("htdemucs_6s", Demucs.BuildArgs("a.wav", "o", "htdemucs_6s"));

    [Fact]
    public void Unknown_model_falls_back_to_htdemucs()
    {
        var a = Demucs.BuildArgs("a.wav", "o", "totally-made-up");
        Assert.Equal("htdemucs", a[a.ToList().IndexOf("-n") + 1]);
    }

    [Fact]
    public void Not_installed_in_a_dev_env()
        => Assert.False(Demucs.Installed);   // no audio-tools/demucs venv here -> graceful degradation path
}
