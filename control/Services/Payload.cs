using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace DokiDex.Control.Services;

// The embedded runtime payload: the scripts/configs the installed app extracts to its home so it's
// self-contained (no cloned repo). It carries runtime files only — NOT the heavy downloaded assets
// (models/, media/), which setup.ps1 fetches. Built + embedded by the csproj StagePayload target.
public static class Payload
{
    public const string ResourceName = "DokiDex.payload.zip";

    public static bool Available =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(ResourceName);

    // Extract the bundled payload to dest. overwriteExisting=false skips files already present (fresh install
    // onto a partially-populated dir); true refreshes scripts (post-update). Never DELETES anything, so
    // models/, media/ and other non-payload data are untouched. Returns false if the payload isn't embedded.
    public static bool ExtractBundledTo(string dest, bool overwriteExisting)
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (s == null) return false;
        using (s) return ExtractZip(s, dest, overwriteExisting);
    }

    // Testable with any zip stream: extract entries under dest (creating dirs), skipping existing files when
    // overwriteExisting is false. Zip-slip-guarded (an entry can't escape dest). Never deletes; returns true.
    public static bool ExtractZip(Stream zip, string dest, bool overwriteExisting)
    {
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var e in archive.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;   // a directory entry
            var target = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && !overwriteExisting) continue;
            e.ExtractToFile(target, overwrite: true);
        }
        return true;
    }
}
