using System.IO;

namespace DokiCode.Control.Services;

// Locates the DokiCode repo root by walking up from the exe (works for both the published
// exe under control\bin\... and `dotnet run`). Falls back to the known install path.
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();
    public static string DokiPs1 => Path.Combine(Root, "doki.ps1");
    public static string VerifyPs1 => Path.Combine(Root, "verify.ps1");
    public static string RunDir => Path.Combine(Root, ".run");

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "doki.ps1"))) return d.FullName;
            d = d.Parent;
        }
        return @"D:\Projects\DokiCode";
    }
}
