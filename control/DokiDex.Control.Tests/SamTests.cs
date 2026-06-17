using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure argv-builder for SAM point segmentation. The model isn't present in a dev env, so SegmentAsync
// degrades to "not installed" (verified at the endpoint); the command construction is locked here.
public class SamTests
{
    [Fact]
    public void Builds_script_image_point_outmask_checkpoint()
    {
        var a = Sam.BuildArgs(@"C:\g\in.png", 120, 80, @"C:\t\m.png");
        Assert.EndsWith("sam-segment.py", a[0]);
        Assert.Equal(@"C:\g\in.png", a[1]);
        Assert.Equal("120", a[2]);
        Assert.Equal("80", a[3]);
        Assert.Equal(@"C:\t\m.png", a[4]);
        Assert.EndsWith("sam_vit_b.pth", a[5]);
        Assert.Equal("vit_b", a[6]);
    }

    [Fact]
    public void Not_installed_in_a_dev_env()
        => Assert.False(Sam.Installed);   // no audio-tools/sam venv+checkpoint here -> graceful degradation path
}
