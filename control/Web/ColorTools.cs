using System;
using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// One color (8-bit sRGB channels).
public readonly record struct Rgb(byte R, byte G, byte B);

// Deterministic, model-FREE color operations — the "high-value local part" the backlog flags (vs flaky
// gen-time palette adherence). All pure + total -> unit-tested; the browser canvas does the image codec and
// ships raw RGBA pixel arrays here (mirrors Blockout: C# does the math, the SPA paints).
//   • DominantColors  — extract a palette from an image (coarse-bucket histogram, top-k by population)
//   • RemapToPalette  — snap every pixel to the nearest palette color in perceptual LAB space (recolor)
//   • RgbToLab / hex  — the supporting perceptual + parsing primitives
public static class ColorTools
{
    // ---- sRGB (D65) -> CIELAB, the perceptual space nearest-color should run in ----
    public static (double L, double A, double B) RgbToLab(byte r, byte g, byte b)
    {
        double R = Inv(r / 255.0), G = Inv(g / 255.0), B = Inv(b / 255.0);
        double x = (R * 0.4124 + G * 0.3576 + B * 0.1805) / 0.95047;
        double y = (R * 0.2126 + G * 0.7152 + B * 0.0722);
        double z = (R * 0.0193 + G * 0.1192 + B * 0.9505) / 1.08883;
        x = F(x); y = F(y); z = F(z);
        return (116 * y - 16, 500 * (x - y), 200 * (y - z));
    }

    private static double Inv(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t + 16.0 / 116.0);

    private static double Dist2((double L, double A, double B) p, (double L, double A, double B) q)
    {
        double dl = p.L - q.L, da = p.A - q.A, db = p.B - q.B;
        return dl * dl + da * da + db * db;
    }

    // Index of the palette color nearest `px` in LAB; -1 if the palette is empty.
    public static int NearestIndex(Rgb px, IReadOnlyList<(double, double, double)> paletteLab)
    {
        int best = -1; double bestD = double.MaxValue;
        var lab = RgbToLab(px.R, px.G, px.B);
        for (int i = 0; i < paletteLab.Count; i++)
        {
            var d = Dist2(lab, paletteLab[i]);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // Snap every pixel of an RGBA buffer to the nearest palette color in LAB; alpha preserved. Empty palette =>
    // a copy unchanged. Returns a new array (non-destructive). Throws on a buffer whose length isn't a multiple
    // of 4 (a malformed RGBA frame) so the endpoint can 400 rather than corrupt.
    public static byte[] RemapToPalette(byte[] rgba, IReadOnlyList<Rgb> palette)
    {
        if (rgba is null) throw new ArgumentNullException(nameof(rgba));
        if (rgba.Length % 4 != 0) throw new ArgumentException("RGBA length must be a multiple of 4", nameof(rgba));
        var outp = (byte[])rgba.Clone();
        if (palette is null || palette.Count == 0) return outp;
        var pal = palette.Select(p => RgbToLab(p.R, p.G, p.B)).ToList();
        for (int i = 0; i < outp.Length; i += 4)
        {
            int idx = NearestIndex(new Rgb(rgba[i], rgba[i + 1], rgba[i + 2]), pal);
            var c = palette[idx];
            outp[i] = c.R; outp[i + 1] = c.G; outp[i + 2] = c.B;   // [i+3] alpha kept
        }
        return outp;
    }

    // Extract up to k dominant colors from an RGBA buffer: bucket by the top 4 bits of each channel (16^3
    // buckets), count near-opaque pixels, take the most-populous buckets and return each bucket's average color.
    // Deterministic (ties break by bucket order). k is clamped to 1..16.
    public static List<Rgb> DominantColors(byte[] rgba, int k)
    {
        k = Math.Clamp(k, 1, 16);
        if (rgba is null || rgba.Length < 4) return new();
        var sumR = new long[4096]; var sumG = new long[4096]; var sumB = new long[4096]; var cnt = new long[4096];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128) continue;   // ignore transparent
            byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            int bucket = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            sumR[bucket] += r; sumG[bucket] += g; sumB[bucket] += b; cnt[bucket]++;
        }
        return Enumerable.Range(0, 4096).Where(i => cnt[i] > 0)
            .OrderByDescending(i => cnt[i]).ThenBy(i => i)
            .Take(k)
            .Select(i => new Rgb((byte)(sumR[i] / cnt[i]), (byte)(sumG[i] / cnt[i]), (byte)(sumB[i] / cnt[i])))
            .ToList();
    }

    // "#rgb" / "#rrggbb" / bare hex -> Rgb; null if malformed.
    public static Rgb? ParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var h = s.Trim().TrimStart('#');
        if (h.Length == 3) h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
        if (h.Length != 6) return null;
        try { return new Rgb(Convert.ToByte(h[..2], 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16)); }
        catch { return null; }
    }

    public static string ToHex(Rgb c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    // Parse a comma/space-separated hex list (the recolor palette query) into colors, dropping malformed entries.
    public static List<Rgb> ParsePalette(string? csv)
        => (csv ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseHex).Where(c => c is not null).Select(c => c!.Value).ToList();
}
