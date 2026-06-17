using System.Diagnostics;
using System.IO;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A browser request to SAM-segment: a base64 image + a click point -> the object's mask.
public sealed record SegmentClickRequest(string? Image, int X, int Y);

// SAM (Segment-Anything) point segmentation — the *semantic* "click an object -> mask" that the magic-wand
// approximates without a model. Runs as a standalone sidecar (own venv, like Demucs/TTS): a dedicated venv
// python runs serving/sam-segment.py against a downloaded checkpoint. The argv build is the pure, unit-tested
// core; the run degrades cleanly when the venv/checkpoint isn't installed (setup.ps1 -Sam) — the same contract
// as the Demucs / TTS clients.
public static class Sam
{
    private static string Dir => Path.Combine(RepoPaths.Root, "audio-tools", "sam");   // reuse the tools area
    private static string VenvPython => Path.Combine(Dir, ".venv", "Scripts", "python.exe");
    private static string Checkpoint => Path.Combine(Dir, "sam_vit_b.pth");
    private static string Script => Path.Combine(RepoPaths.Root, "serving", "sam-segment.py");

    public static bool Installed => File.Exists(VenvPython) && File.Exists(Checkpoint);

    // Pure: the python argv to run the segment script. Unit-tested with no model present.
    public static IReadOnlyList<string> BuildArgs(string image, int x, int y, string outMask)
        => new[] { Script, image, x.ToString(), y.ToString(), outMask, Checkpoint, "vit_b" };

    public sealed record Result(bool Ok, string? MaskPath, string? Message);

    public static async Task<Result> SegmentAsync(string imagePath, int x, int y, CancellationToken ct)
    {
        if (!File.Exists(imagePath)) return new Result(false, null, "image not found");
        if (!Installed) return new Result(false, null,
            "SAM not installed — run  .\\setup.ps1 -Sam  (creates the SAM venv + downloads the checkpoint). The magic-wand works without it.");

        var outMask = Path.Combine(Path.GetTempPath(), $"dokidex-sammask-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo(VenvPython) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in BuildArgs(imagePath, x, y, outMask)) psi.ArgumentList.Add(a);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var p = Process.Start(psi);
            if (p is null) return new Result(false, null, "could not start SAM");
            var err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            if (p.ExitCode != 0 || !File.Exists(outMask)) return new Result(false, null, $"SAM failed: {err}");
            return new Result(true, outMask, "done");
        }
        catch (OperationCanceledException) { return new Result(false, null, "cancelled / timed out"); }
        catch (Exception ex) { return new Result(false, null, $"SAM error: {ex.Message}"); }
    }
}
