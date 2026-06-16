using System.IO;
using System.Linq;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// Lists the installed LoRAs (SwarmUI Models/Lora) for the LoRA-mixer UI. SwarmUI references a LoRA by its
// name RELATIVE to the Lora root, WITHOUT extension, forward-slashed (so <lora:subdir/name:0.8> works) —
// that's exactly the string this returns. File-based + graceful (empty when none installed), like the gallery
// / model manager.
public static class Loras
{
    private static readonly string[] Ext = { ".safetensors", ".ckpt", ".pt", ".pth" };

    // SwarmUI references a model by its name relative to its model-class root, extensionless + forward-slashed
    // (so <lora:subdir/name> / a ControlNet model dropdown value both work). Returns exactly those strings;
    // file-based + graceful-empty (like the gallery / model manager).
    private static IReadOnlyList<string> Scan(string subdir)
    {
        var root = Path.Combine(RepoPaths.Root, "media", "SwarmUI", "Models", subdir);
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => Ext.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.ChangeExtension(Path.GetRelativePath(root, f), null)!.Replace('\\', '/'))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> List() => Scan("Lora");
    public static IReadOnlyList<string> ControlNets() => Scan("controlnet");
}
