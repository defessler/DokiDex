using System.Diagnostics;
using System.IO;

namespace DokiDex.Control.Services;

// Resolves the DokiDex home (the install/adopted root) and the launched-exe directory. The app is
// independent of any cloned repo: it prefers a saved InstallRoot, then (for dev) walks up to find
// doki.ps1, then falls back to the exe's own dir — never a hardcoded path to the old location.
public static class RepoPaths
{
    // Directory of the LAUNCHED exe. Under a PublishSingleFile self-contained build, AppContext.BaseDirectory
    // is the per-run self-extraction TEMP dir, NOT where the user launched the exe — so use Environment.ProcessPath
    // (the apphost), the same API Updater.cs relies on. Falls back to MainModule, then BaseDirectory.
    public static string ExeDir { get; } = ComputeExeDir();
    public static string Root { get; private set; } = FindRoot();
    public static string DokiPs1 => Path.Combine(Root, "doki.ps1");
    public static string VerifyPs1 => Path.Combine(Root, "verify.ps1");
    public static string RunDir => Path.Combine(Root, ".run");

    // A root is usable only when it actually contains doki.ps1 — the gate App/MainViewModel use to decide
    // whether to boot the manager or prompt the user to locate/adopt a DokiDex home.
    public static bool HasValidRoot => File.Exists(Path.Combine(Root, "doki.ps1"));

    // Re-resolve Root after the install location changes at runtime (e.g. the user adopts/locates a folder).
    public static void Refresh() => Root = FindRoot();

    private static string ComputeExeDir()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
        var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }

    private static string FindRoot() => ResolveRoot(AppSettings.Load().InstallRoot, ExeDir, AppContext.BaseDirectory);

    // Pure resolution (unit-tested): a valid configured install root wins; else walk UP from each start
    // looking for doki.ps1; else fall back to the first start (the launched-exe dir) — never a hardcoded path.
    internal static string ResolveRoot(string? configuredInstallRoot, params string[] walkUpStarts)
    {
        if (!string.IsNullOrWhiteSpace(configuredInstallRoot)
            && File.Exists(Path.Combine(configuredInstallRoot!, "doki.ps1")))
            return configuredInstallRoot!;
        foreach (var start in walkUpStarts)
        {
            var d = new DirectoryInfo(start);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "doki.ps1"))) return d.FullName;
                d = d.Parent;
            }
        }
        return walkUpStarts.Length > 0 ? walkUpStarts[0] : AppContext.BaseDirectory;
    }
}
