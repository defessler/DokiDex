using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DokiDex.Control.Services;

/// <summary>
/// Silent in-place auto-updater for the DokiDex control panel. Checks
/// github.com/defessler/DokiDex/releases for a newer panel build, downloads it to a staging dir,
/// and swaps it into place by copying the verified bytes BESIDE the running exe, then doing
/// same-volume renames — so the expensive/failable (cross-volume) copy happens while the running
/// image is still intact, and the actual swap is just renames that can't fail mid-stream. No helper
/// process, no admin, no installer. The panel is repo-coupled (it shells doki.ps1), so the released
/// exe lives INSIDE the cloned repo; only the exe self-updates — scripts come via git.
/// </summary>
public static class Updater
{
    public const string Repo = "defessler/DokiDex";
    public const string AssetPrefix = "DokiDex-";
    public const string AssetSuffix = "-win-x64.exe";

    static readonly string UpdateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dokidex", "update");

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("dokidex-control");
        return h;
    }

    // ---- version helpers ----

    /// <summary>Running panel version as "vX.Y.Z" (matches the GitHub tag). Reads the ENTRY assembly
    /// (set via -p:Version in the release build); a dev build reports the csproj baseline (v0.1.0).</summary>
    public static string RunningVersion() =>
        "v" + ((System.Reflection.Assembly.GetEntryAssembly() ?? System.Reflection.Assembly.GetExecutingAssembly())
               .GetName().Version?.ToString(3) ?? "0.0.0");

    public static bool IsNewer(string latest, string running)
    {
        if (!System.Version.TryParse(Core(latest), out var l)) return false;
        if (!System.Version.TryParse(Core(running), out var r)) return false;
        return l > r;
        // strip leading 'v' AND any pre-release suffix ("v1.0.0-rc1" -> "1.0.0") so System.Version parses
        static string Core(string tag) => tag.TrimStart('v', 'V').Split('-')[0];
    }

    /// <summary>Extract the FULL release tag from a staged asset filename ("DokiDex-{tag}-win-x64.exe"),
    /// INCLUDING any hyphenated pre-release suffix (a naive Split('-') truncates "v1.0.0-rc1").</summary>
    public static string? TagFromAssetFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var suf = AssetSuffix.Replace(".exe", "");   // filename without extension drops ".exe"
        return name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suf, StringComparison.OrdinalIgnoreCase)
            && name.Length > AssetPrefix.Length + suf.Length
            ? name.Substring(AssetPrefix.Length, name.Length - AssetPrefix.Length - suf.Length)
            : null;
    }

    // ---- GitHub API ----

    public sealed record ReleaseInfo(string Tag, string Body, long SizeBytes);

    /// <summary>Latest release tag + notes + asset size, or null on network/parse error / no matching asset.</summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var n = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    if (n.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
                    { size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0; break; }
                }
            return string.IsNullOrEmpty(tag) ? null : new ReleaseInfo(tag, body, size);
        }
        catch { return null; }
    }

    /// <summary>Convenience: the latest release if it is strictly newer than what is running, else null.</summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var info = await GetLatestReleaseInfoAsync(ct);
        return info != null && IsNewer(info.Tag, RunningVersion()) ? info : null;
    }

    // ---- staging ----

    static string StagedPath(string tag) => Path.Combine(UpdateDir, $"{AssetPrefix}{tag}{AssetSuffix}");

    /// <summary>The staged update with the HIGHEST tag newer than running, else null. (Highest, not the
    /// first Directory.GetFiles hit, so a stale older-but-still-newer leftover can't shadow a fresh one.)</summary>
    public static (string path, string tag)? FindStagedUpdate()
    {
        if (!Directory.Exists(UpdateDir)) return null;
        var running = RunningVersion();
        (string path, string tag)? best = null;
        foreach (var f in Directory.GetFiles(UpdateDir, $"{AssetPrefix}v*{AssetSuffix}"))
        {
            var tag = TagFromAssetFile(f);
            if (tag == null || !IsNewer(tag, running)) continue;
            if (best == null || IsNewer(tag, best.Value.tag)) best = (f, tag);
        }
        return best;
    }

    /// <summary>Download the release asset for <paramref name="tag"/> to staging, reporting 0-100 progress.
    /// Prunes any prior staged builds first, and FAILS (deletes the partial) on a truncated body so a
    /// short download can never be promoted into the running exe.</summary>
    public static async Task<bool> DownloadUpdateAsync(string tag, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var url = $"https://github.com/{Repo}/releases/download/{tag}/{AssetPrefix}{tag}{AssetSuffix}";
        var dest = StagedPath(tag);
        var tmp = dest + ".tmp";
        try
        {
            Directory.CreateDirectory(UpdateDir);
            foreach (var old in Directory.GetFiles(UpdateDir, $"{AssetPrefix}v*{AssetSuffix}"))   // prune leftovers
                if (!string.Equals(old, dest, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }

            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            var buf = new byte[81920];
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using (var fs = File.Create(tmp))
            {
                int read;
                while ((read = await stream.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0) progress?.Report(downloaded * 100.0 / total);
                }
            }
            if (total > 0 && downloaded != total) { try { File.Delete(tmp); } catch { } return false; }  // truncated -> reject
            File.Move(tmp, dest, overwrite: true);
            progress?.Report(100);
            return true;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }

    // ---- apply ----

    static bool LooksLikeCompleteExe(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 4096) return false;     // a real self-contained WPF exe is tens of MB
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';  // PE/MZ header
        }
        catch { return false; }
    }

    /// <summary>Swap a verified staged build into <paramref name="currentExe"/> (same path/filename).
    /// SAFE ORDER: copy staged -&gt; ".new" BESIDE currentExe (so the failable, possibly cross-volume copy
    /// happens with the running image still present), verify it's a complete PE, THEN do same-volume
    /// renames (running image -&gt; ".old", ".new" -&gt; currentExe) which cannot fail mid-stream. On any
    /// failure the running exe is left intact. Internal for tests.</summary>
    internal static bool TryApplyStaged(string stagedPath, string currentExe)
    {
        var newFile = currentExe + ".new";
        var old = currentExe + ".old";
        try
        {
            try { File.Delete(newFile); } catch { }
            File.Copy(stagedPath, newFile, overwrite: true);          // 1. expensive copy, running exe untouched
            if (!LooksLikeCompleteExe(newFile)) { try { File.Delete(newFile); } catch { } return false; }  // 2. verify

            try { File.Delete(old); } catch { }
            File.Move(currentExe, old);                               // 3a. rename running image out (allowed)
            try { File.Move(newFile, currentExe); return true; }      // 3b. same-volume move-in (atomic-ish)
            catch
            {
                try { if (!File.Exists(currentExe) && File.Exists(old)) File.Move(old, currentExe); } catch { }  // rollback
                try { File.Delete(newFile); } catch { }
                return false;
            }
        }
        catch { try { File.Delete(newFile); } catch { } return false; }
    }

    /// <summary>Apply the highest staged update IN PLACE and return that path to relaunch, or null if
    /// nothing is staged / the swap failed. The running exe is never left missing (see TryApplyStaged).
    /// The ".old" sidecar is swept by <see cref="CleanUpSuperseded"/> next launch.</summary>
    public static string? ApplyInPlaceNow(string currentExe)
    {
        var staged = FindStagedUpdate();
        if (staged == null) return null;
        if (!TryApplyStaged(staged.Value.path, currentExe)) return null;
        try { File.Delete(staged.Value.path); } catch { }   // consume the staged file so a fresh launch finds nothing newer
        return currentExe;
    }

    /// <summary>Sweep "*.exe.old" / "*.exe.new" update sidecars from the exe's dir (best-effort; the
    /// .old of the image we updated FROM is locked while its process exits, so it lands next launch).</summary>
    public static void CleanUpSuperseded(string currentExe)
    {
        try
        {
            var dir = Path.GetDirectoryName(currentExe);
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.exe.old")) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(dir, "*.exe.new")) try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
