using System.IO;
using System.Text.Json;

namespace DokiDex.Control.Services;

// Tiny user-settings store under %LocalAppData%\dokidex (the same root the updater staging + crash log already
// use). Best-effort: it NEVER throws — a storage hiccup just yields defaults. Today it carries one thing, the
// DokiGen Studio output folder; add fields here as the panel grows settings.
public sealed class AppSettings
{
    public string? GenOutputDir { get; set; }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dokidex");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Opts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }
}
