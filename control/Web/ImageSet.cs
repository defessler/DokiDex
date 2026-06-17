using System.Collections.Generic;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// One cell of a series: its own prompt and its own seed (so re-rolling one cell reseeds only that cell).
public sealed record SeriesCell(string? Prompt, int Seed = -1);

// An Image Set / series request: a shared style + aspect (+ negative) locked across every cell, each cell with
// its own prompt. `Fast` defaults on — a series is an exploration grid.
public sealed record SeriesSpec(string? Style, string? Aspect, string? Negative, List<SeriesCell>? Cells, bool Fast = true);

// Image Set / series object (the missing emote/icon/turnaround primitive): N cells share ONE locked style +
// aspect + negative, but each keeps its own prompt and seed. Compile is pure -> unit-tested; the shared style
// is MERGED into each cell's prompt, so the existing per-card "rerun" already rerolls a single cell (same
// style, fresh seed) with no special grid plumbing.
public static class ImageSet
{
    public static List<GenRequest> Compile(SeriesSpec spec)
    {
        var style = (spec.Style ?? "").Trim();
        var asp = string.IsNullOrWhiteSpace(spec.Aspect) ? null : spec.Aspect!.Trim();
        var neg = string.IsNullOrWhiteSpace(spec.Negative) ? null : spec.Negative!.Trim();
        var outp = new List<GenRequest>();
        foreach (var c in spec.Cells ?? new())
        {
            var p = (c?.Prompt ?? "").Trim();
            if (p.Length == 0 && style.Length == 0) continue;   // skip wholly empty cells
            var prompt = style.Length > 0 ? (p.Length > 0 ? $"{p}, {style}" : style) : p;
            outp.Add(new GenRequest(prompt, "image", Fast: spec.Fast, Seed: c!.Seed, Aspect: asp, Negative: neg));
        }
        return outp;
    }
}
