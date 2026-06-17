using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DokiDex.Web;

// Dependency-free WAV concatenation: stitch several PCM WAV clips (Chatterbox renders one per dialogue line)
// into one file with NO ffmpeg — parse the RIFF chunks, take the first clip's fmt as canonical, append every
// clip's data payload, and write a fresh header with corrected sizes. Pure (byte[] in, byte[] out) -> unit-
// tested. Clips are assumed same-format (one engine, one voice config produces consistent output).
public static class WavTools
{
    // Concatenate WAV clips; unparseable clips are skipped. Returns null if none are valid PCM WAVs.
    public static byte[]? Concat(IReadOnlyList<byte[]> clips)
    {
        byte[]? fmt = null;
        var datas = new List<byte[]>();
        long total = 0;
        foreach (var c in clips ?? Array.Empty<byte[]>())
        {
            if (!TryParse(c, out var f, out var data)) continue;
            fmt ??= f;
            datas.Add(data);
            total += data.Length;
        }
        if (fmt is null || datas.Count == 0) return null;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)(4 + (8 + fmt.Length) + (8 + total)));   // size of everything after this field
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write((uint)fmt.Length);
        w.Write(fmt);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((uint)total);
        foreach (var d in datas) w.Write(d);
        w.Flush();
        return ms.ToArray();
    }

    // Pull the fmt-chunk payload and the data-chunk payload out of one WAV. False if it isn't RIFF/WAVE with both.
    private static bool TryParse(byte[]? b, out byte[] fmt, out byte[] data)
    {
        fmt = Array.Empty<byte>(); data = Array.Empty<byte>();
        if (b is null || b.Length < 12) return false;
        if (Tag(b, 0) != "RIFF" || Tag(b, 8) != "WAVE") return false;
        int pos = 12; bool haveFmt = false, haveData = false;
        while (pos + 8 <= b.Length)
        {
            var id = Tag(b, pos);
            uint size = BitConverter.ToUInt32(b, pos + 4);
            int payload = pos + 8;
            if (payload + (long)size > b.Length) size = (uint)Math.Max(0, b.Length - payload);   // tolerate a clipped tail
            if (id == "fmt ") { fmt = Slice(b, payload, (int)size); haveFmt = true; }
            else if (id == "data") { data = Slice(b, payload, (int)size); haveData = true; }
            pos = payload + (int)size + ((size & 1) == 1 ? 1 : 0);   // chunks are word-aligned
        }
        return haveFmt && haveData;
    }

    private static string Tag(byte[] b, int o) => Encoding.ASCII.GetString(b, o, 4);
    private static byte[] Slice(byte[] b, int o, int n) { var r = new byte[n]; Array.Copy(b, o, r, 0, n); return r; }
}
