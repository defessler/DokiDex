using System.Diagnostics;
using System.IO;
using System.Linq;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A browser request to split an audio file (a gallery item name) into stems.
public sealed record StemRequest(string? Name, string? Model);

// Local stem separation via Demucs — a standalone, model-free DSP tool that runs offline on GPU or CPU
// (htdemucs / htdemucs_6s). Follows the repo's standalone-sidecar pattern (own venv, like Chatterbox): a
// dedicated venv python at <home>/audio-tools/demucs runs `python -m demucs`. The command construction is the
// pure, unit-tested core; the run degrades cleanly when the venv isn't installed (setup.ps1 -Demucs) — the
// same contract as the TTS / SwarmUI clients.
public static class Demucs
{
    private static readonly string[] Stems = { "vocals", "drums", "bass", "other", "guitar", "piano" };

    private static string VenvPython =>
        Path.Combine(RepoPaths.Root, "audio-tools", "demucs", ".venv", "Scripts", "python.exe");

    public static bool Installed => File.Exists(VenvPython);

    // Pure: the `python -m demucs` argv. `-n <model>` picks 4-stem (htdemucs) or 6-stem (htdemucs_6s);
    // -o <out> sets the output root; the track path is last. Unit-tested with no tool present.
    public static IReadOnlyList<string> BuildArgs(string audioPath, string outDir, string model)
    {
        var m = model is "htdemucs_6s" or "htdemucs_ft" or "htdemucs" or "mdx_extra" ? model : "htdemucs";
        return new[] { "-m", "demucs", "-n", m, "-o", outDir, audioPath };
    }

    public sealed record Result(bool Ok, IReadOnlyList<string> Stems, string? Message);

    public static async Task<Result> SeparateAsync(string audioPath, string? model, CancellationToken ct)
    {
        if (!File.Exists(audioPath)) return new Result(false, Array.Empty<string>(), "audio file not found");
        if (!Installed) return new Result(false, Array.Empty<string>(),
            "Demucs not installed — run  .\\setup.ps1 -Demucs  (creates the audio-tools/demucs venv)");

        var outDir = Path.Combine(DokiService.GenDir, "stems");
        Directory.CreateDirectory(outDir);
        var args = BuildArgs(audioPath, outDir, model ?? "htdemucs");
        try
        {
            var psi = new ProcessStartInfo(VenvPython) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            var p = Process.Start(psi);
            if (p is null) return new Result(false, Array.Empty<string>(), "could not start demucs");
            var err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            if (p.ExitCode != 0) return new Result(false, Array.Empty<string>(), $"demucs failed: {err.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}");

            // demucs writes <out>/<model>/<trackname>/<stem>.wav — collect them by known stem names.
            var track = Path.GetFileNameWithoutExtension(audioPath);
            var found = Directory.Exists(outDir)
                ? Directory.EnumerateFiles(outDir, "*.wav", SearchOption.AllDirectories)
                    .Where(f => Stems.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                             && Path.GetFileName(Path.GetDirectoryName(f)!) == track)
                    .ToList()
                : new System.Collections.Generic.List<string>();
            return found.Count > 0
                ? new Result(true, found, "done")
                : new Result(false, Array.Empty<string>(), "demucs produced no stems");
        }
        catch (OperationCanceledException) { return new Result(false, Array.Empty<string>(), "cancelled / timed out"); }
        catch (Exception ex) { return new Result(false, Array.Empty<string>(), $"demucs error: {ex.Message}"); }
    }
}
