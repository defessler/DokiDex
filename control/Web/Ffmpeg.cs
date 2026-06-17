using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// Extract a keyframe from a gallery clip (last = the i2v-extend keyframe); join several gallery clips in order.
public sealed record FrameRequest(string? Name, bool Last = true);
public sealed record JoinRequest(List<string>? Names);

// Gated ffmpeg video tools — the practical core of the video-composition backlog (clip-extend / storyboard
// strip) without the heavy async-gen orchestrator: extract a frame from a clip (→ an i2v/FLF2V keyframe) and
// concatenate clips into one. The argv builders are pure + unit-tested; execution is gated on ffmpeg being
// found (PATH or a bundled location) and degrades cleanly when it isn't — the same contract as SAM/Demucs.
public static class Ffmpeg
{
    private static string? _exe;
    private static bool _looked;

    // Locate ffmpeg once: a bundled copy under media\, else the first ffmpeg.exe on PATH. Null if not found.
    public static string? Exe
    {
        get
        {
            if (_looked) return _exe;
            _looked = true;
            foreach (var c in new[]
            {
                Path.Combine(RepoPaths.Root, "media", "SwarmUI", "ffmpeg.exe"),
                Path.Combine(RepoPaths.Root, "media", "ffmpeg.exe"),
            })
                if (File.Exists(c)) { _exe = c; return _exe; }
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) { _exe = p; return _exe; } } catch { }
            }
            return _exe;   // null
        }
    }

    public static bool Installed => Exe is not null;

    // Pure: extract ONE frame to a PNG — the last frame (sseof, the i2v-extend keyframe) or the first.
    public static IReadOnlyList<string> ExtractFrameArgs(string input, string outPath, bool last)
        => last
            ? new[] { "-y", "-sseof", "-1", "-i", input, "-update", "1", "-frames:v", "1", outPath }
            : new[] { "-y", "-i", input, "-frames:v", "1", outPath };

    // Pure: concatenate N clips (video stream only, re-encoded for robustness across mixed encodings) into one
    // mp4. filter_complex concat tolerates different codecs/resolutions where the concat demuxer would not.
    public static IReadOnlyList<string> ConcatArgs(IReadOnlyList<string> inputs, string outPath)
    {
        var a = new List<string> { "-y" };
        foreach (var i in inputs) { a.Add("-i"); a.Add(i); }
        var streams = string.Concat(Enumerable.Range(0, inputs.Count).Select(i => $"[{i}:v]"));
        a.Add("-filter_complex"); a.Add($"{streams}concat=n={inputs.Count}:v=1:a=0[v]");
        a.Add("-map"); a.Add("[v]");
        a.Add(outPath);
        return a;
    }

    public sealed record Result(bool Ok, string? Message);

    public static async Task<Result> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = Exe;
        if (exe is null) return new Result(false, "ffmpeg not found — install it on PATH (or bundle media\\ffmpeg.exe) to join/extract video.");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            var p = Process.Start(psi);
            if (p is null) return new Result(false, "could not start ffmpeg");
            var err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return p.ExitCode == 0 ? new Result(true, "done") : new Result(false, $"ffmpeg failed: {Tail(err)}");
        }
        catch (OperationCanceledException) { return new Result(false, "cancelled / timed out"); }
        catch (Exception ex) { return new Result(false, $"ffmpeg error: {ex.Message}"); }
    }

    private static string Tail(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim().Split('\n').Last().Trim();
}
