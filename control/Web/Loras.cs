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
    private static string Root => Path.Combine(RepoPaths.Root, "media", "SwarmUI", "Models", "Lora");
    private static readonly string[] Ext = { ".safetensors", ".ckpt", ".pt" };

    public static IEnumerable<string> List()
    {
        var root = Root;
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => Ext.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.ChangeExtension(Path.GetRelativePath(root, f), null)!.Replace('\\', '/'))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
