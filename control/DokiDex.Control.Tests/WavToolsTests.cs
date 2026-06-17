using System.Collections.Generic;
using System.IO;
using System.Text;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The dependency-free WAV concatenator (stitches per-line dialogue clips with no ffmpeg).
public class WavToolsTests
{
    // a minimal canonical PCM WAV (16-byte fmt) wrapping `data`
    private static byte[] MakeWav(byte[] data)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)(4 + 24 + 8 + data.Length));
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write((uint)16);
        w.Write((ushort)1);      // PCM
        w.Write((ushort)1);      // mono
        w.Write((uint)8000);     // sample rate
        w.Write((uint)16000);    // byte rate
        w.Write((ushort)2);      // block align
        w.Write((ushort)16);     // bits
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((uint)data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Concat_joins_the_pcm_payloads_under_one_header()
    {
        var a = new byte[] { 1, 2, 3, 4 };
        var b = new byte[] { 5, 6, 7, 8 };
        var outp = WavTools.Concat(new List<byte[]> { MakeWav(a), MakeWav(b) });
        Assert.NotNull(outp);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(outp!, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(outp, 8, 4));
        Assert.Equal(44 + a.Length + b.Length, outp.Length);            // canonical 44-byte header + both payloads
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, outp[44..]); // payloads in order
    }

    [Fact]
    public void Concat_skips_unparseable_clips()
    {
        var good = MakeWav(new byte[] { 9, 9 });
        var outp = WavTools.Concat(new List<byte[]> { new byte[] { 0, 1, 2 }, good });
        Assert.NotNull(outp);
        Assert.Equal(new byte[] { 9, 9 }, outp![44..]);
    }

    [Fact]
    public void Concat_returns_null_when_nothing_is_valid()
        => Assert.Null(WavTools.Concat(new List<byte[]> { new byte[] { 1, 2, 3 }, System.Array.Empty<byte>() }));

    [Fact]
    public void Concat_of_a_single_clip_round_trips_the_data()
    {
        var outp = WavTools.Concat(new List<byte[]> { MakeWav(new byte[] { 7, 7, 7, 7 }) });
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, outp![44..]);
    }
}
