using System.Diagnostics;
using System.IO;
using System.Linq;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A browser request to train a LoRA from gallery images.
public sealed record TrainRequest(string? Name, List<string>? Images, string? BaseModel, int Steps = 1600, int Dim = 16);

// In-app LoRA training via kohya sd-scripts — a gated sidecar (own venv, like Demucs/SAM): a venv runs
// train_network.py. The output LoRA lands in SwarmUI's Lora folder, so it shows up in the LoRA mixer (a clean
// loop). The argv build is the pure, unit-tested core; the run degrades cleanly when sd-scripts isn't installed
// (setup.ps1 -Train). The training itself is GPU work and depends on sd-scripts supporting the base model arch
// — the integration ships now; the run happens where the trainer + a compatible base exist.
public static class Training
{
    private static string Dir => Path.Combine(RepoPaths.Root, "audio-tools", "sd-scripts");
    private static string Accelerate => Path.Combine(Dir, ".venv", "Scripts", "accelerate.exe");
    private static string TrainScript => Path.Combine(Dir, "train_network.py");
    private static string LoraOut => Path.Combine(RepoPaths.Root, "media", "SwarmUI", "Models", "Lora");

    public static bool Installed => File.Exists(Accelerate) && File.Exists(TrainScript);

    // Pure: the train_network.py argv for a standard LoRA train. Unit-tested with no trainer present.
    public static IReadOnlyList<string> BuildArgs(string baseModel, string dataDir, string outDir, string name, int steps, int dim)
    {
        steps = System.Math.Clamp(steps, 100, 20000);
        dim = System.Math.Clamp(dim, 4, 128);
        return new[]
        {
            "launch", TrainScript,
            "--pretrained_model_name_or_path", baseModel,
            "--train_data_dir", dataDir,
            "--output_dir", outDir,
            "--output_name", name,
            "--network_module", "networks.lora",
            "--network_dim", dim.ToString(),
            "--network_alpha", dim.ToString(),
            "--resolution", "1024,1024",
            "--max_train_steps", steps.ToString(),
            "--learning_rate", "1e-4",
            "--mixed_precision", "bf16",
            "--save_model_as", "safetensors",
            "--cache_latents",
            "--optimizer_type", "AdamW8bit",
        };
    }

    public sealed record Result(bool Ok, string? Message);

    public static async Task<Result> TrainAsync(TrainRequest req, GalleryService gal, CancellationToken ct)
    {
        var name = RecipeStore.SafeName(req.Name);   // reuse the safe-name guard (file stem)
        if (name is null) return new Result(false, "bad LoRA name");
        var imgs = (req.Images ?? new()).Select(gal.Resolve).Where(p => p is not null).Cast<string>().ToList();
        if (imgs.Count == 0) return new Result(false, "select at least one training image from the Library");
        if (!Installed) return new Result(false,
            "trainer not installed — run  .\\setup.ps1 -Train  (clones kohya sd-scripts + venv). Training needs the GPU.");

        var baseModel = req.BaseModel;
        if (string.IsNullOrWhiteSpace(baseModel)) return new Result(false, "pick a base model to train against");

        // kohya dataset layout: <dataDir>/<repeats>_<concept>/<images>
        var dataDir = Path.Combine(Path.GetTempPath(), $"dokidex-train-{name}-{Guid.NewGuid():N}");
        var concept = Path.Combine(dataDir, $"10_{name}");
        try
        {
            Directory.CreateDirectory(concept);
            foreach (var src in imgs) File.Copy(src, Path.Combine(concept, Path.GetFileName(src)), true);
            Directory.CreateDirectory(LoraOut);

            var psi = new ProcessStartInfo(Accelerate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = Dir };
            foreach (var a in BuildArgs(baseModel, dataDir, LoraOut, name, req.Steps, req.Dim)) psi.ArgumentList.Add(a);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromHours(6));
            var p = Process.Start(psi);
            if (p is null) return new Result(false, "could not start the trainer");
            var err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return p.ExitCode == 0
                ? new Result(true, $"trained {name}.safetensors -> it's now in the LoRA mixer")
                : new Result(false, $"training failed (does sd-scripts support this base arch?): {err.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}");
        }
        catch (OperationCanceledException) { return new Result(false, "cancelled / timed out"); }
        catch (Exception ex) { return new Result(false, $"training error: {ex.Message}"); }
    }
}
