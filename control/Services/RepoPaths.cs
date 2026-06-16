using System.Diagnostics;
using System.IO;

namespace DokiDex.Control.Services;

// Locates the launched-exe directory and the DokiDex repo root. Works for both the published single-file
// exe and `dotnet run`.
public static class RepoPaths
{
    // Directory of the LAUNCHED exe. Under a PublishSingleFile self-contained build, AppContext.BaseDirectory
    // is the per-run self-extraction TEMP dir, NOT where the user launched the exe — so use Environment.ProcessPath
    // (the apphost), the same API Updater.cs already relies on. Falls back to MainModule, then BaseDirectory.
    public static string ExeDir { get; } = ComputeExeDir();
    public static string Root { get; } = FindRoot();
    public static string DokiPs1 => Path.Combine(Root, "doki.ps1");
    public static string VerifyPs1 => Path.Combine(Root, "verify.ps1");
    public static string RunDir => Path.Combine(Root, ".run");

    private static string ComputeExeDir()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
        var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }

    private static string FindRoot()
    {
        // Try the launched-exe dir first (correct for the published single-file exe), then BaseDirectory
        // (the bin\ tree under `dotnet run`). A stale absolute fallback is what broke when the project moved,
        // so the last resort is the exe dir itself — never a hardcoded path to the old location.
        foreach (var start in new[] { ExeDir, AppContext.BaseDirectory })
        {
            var d = new DirectoryInfo(start);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "doki.ps1"))) return d.FullName;
                d = d.Parent;
            }
        }
        return ExeDir;
    }
}
