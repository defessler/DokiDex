using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure ffmpeg argv builders (frame extraction + clip concat). No process, no ffmpeg needed.
public class FfmpegTests
{
    [Fact]
    public void ExtractFrameArgs_last_uses_sseof_and_one_frame()
    {
        var a = Ffmpeg.ExtractFrameArgs(@"C:\g\clip.mp4", @"C:\g\out.png", last: true);
        Assert.Contains("-sseof", a);
        var i = a.ToList().IndexOf("-i");
        Assert.Equal(@"C:\g\clip.mp4", a[i + 1]);
        Assert.Equal(@"C:\g\out.png", a[^1]);
        Assert.Equal("1", a[a.ToList().IndexOf("-frames:v") + 1]);
    }

    [Fact]
    public void ExtractFrameArgs_first_has_no_sseof()
        => Assert.DoesNotContain("-sseof", Ffmpeg.ExtractFrameArgs("in.mp4", "out.png", last: false));

    [Fact]
    public void ConcatArgs_lists_every_input_and_builds_the_filter()
    {
        var a = Ffmpeg.ConcatArgs(new List<string> { "a.mp4", "b.mp4", "c.mp4" }, "out.mp4").ToList();
        Assert.Equal(3, a.Count(x => x == "-i"));
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Equal("[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", fc);
        Assert.Equal("[v]", a[a.IndexOf("-map") + 1]);
        Assert.Equal("out.mp4", a[^1]);
    }

    [Fact]
    public void ConcatArgs_for_two_clips_counts_two()
    {
        var a = Ffmpeg.ConcatArgs(new List<string> { "a.mp4", "b.mp4" }, "o.mp4").ToList();
        Assert.Contains("[0:v][1:v]concat=n=2:v=1:a=0[v]", a);
    }
}
