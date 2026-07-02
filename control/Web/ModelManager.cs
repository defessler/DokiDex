using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// Model & workflow manager. Reads media-assets/model-catalog.json (capability-mapped), reports which models
// are installed (file presence under media/SwarmUI/Models/), installs them by direct download (atomic
// .part -> move, with progress), and removes them. Single-user, so a small in-memory download map.
// (P3b will add SwarmUI DoModelDownloadWS for token-gated Civitai/HF + TriggerRefresh + Add-by-URL.)
//
// Optional integrity check: an entry may carry a "sha256" field. When present, DownloadAsync hashes the
// stream incrementally as it copies (same one-pass approach as LlmModelManager.InstallAsync -- no second
// read over a multi-GB file) and verifies before promoting the .part; a mismatch deletes the .part and
// surfaces a per-entry error via the normal downloading/failed status. Entries with no sha256 (the common
// case until G1 fills in verified values) keep the exact prior behavior, plus a one-line console warning.
public sealed class ModelManager
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };  // multi-GB files
    private readonly ConcurrentDictionary<string, DlState> _downloads = new();
    private List<CatalogEntry>? _catalog;

    private sealed class CatalogEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Capability { get; set; } = "";
        public string Tier { get; set; } = "full";
        [JsonPropertyName("default")] public bool Default { get; set; }
        public double SizeGb { get; set; }
        public string File { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Sha256 { get; set; }
    }
    private sealed class DlState { public double Progress; public string Status = "downloading"; public string? Message; }
    private sealed record Catalog(List<CatalogEntry> Models);

    // Verify decision for an optional catalog sha256: no expected value (null/empty/whitespace) means the
    // entry hasn't opted in yet -- pass through with no check. Otherwise a case-insensitive compare; mismatch
    // is a hard failure naming the file. Unlike LlmModelManager.VerifyDecision this catalog has no exact byte
    // size (SizeGb is an approximate estimate for the UI, not a verifiable count) and no "UNVERIFIED" sentinel
    // (this check is opt-in per entry, not a G1-mandatory gate) -- so this is a smaller, media-specific seam
    // rather than a reuse of that one.
    internal static string? VerifySha256(string fileName, string? expectedSha256, string actualSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return null;
        return string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{fileName}: sha256 mismatch (expected {expectedSha256}, got {actualSha256}) -- deleted the download.";
    }

    private static string SwarmModelsRoot => Path.Combine(RepoPaths.Root, "media", "SwarmUI", "Models");

    private List<CatalogEntry> Entries()
    {
        if (_catalog is not null) return _catalog;
        try
        {
            var p = Path.Combine(RepoPaths.Root, "media-assets", "model-catalog.json");
            var c = File.Exists(p)
                ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(p), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
            _catalog = c?.Models ?? new();
        }
        catch { _catalog = new(); }
        return _catalog;
    }

    private static string PathFor(CatalogEntry e) => Path.Combine(SwarmModelsRoot, e.File.Replace('/', Path.DirectorySeparatorChar));

    public IEnumerable<object> List() => Entries().Select(e =>
    {
        _downloads.TryGetValue(e.Id, out var dl);
        return (object)new
        {
            id = e.Id, name = e.Name, capability = e.Capability, tier = e.Tier, isDefault = e.Default, sizeGb = e.SizeGb,
            installed = File.Exists(PathFor(e)),
            downloading = dl is { Status: "downloading" },
            progress = dl?.Progress ?? 0,
            error = dl is { Status: "failed" } ? dl.Message : null,
        };
    });

    // Installed image checkpoints as routable models (File = the SwarmUI model name = basename w/ extension,
    // matching the recipe's `model` convention). Feeds the manual picker + the Auto router.
    public IReadOnlyList<RoutableModel> InstalledImageModels() => Entries()
        .Where(e => e.Capability == "image" && File.Exists(PathFor(e)))
        .Select(e => new RoutableModel(e.Id, Path.GetFileName(e.File), e.Name, e.Default))
        .ToList();

    // Every catalog id + capability tag for entries actually installed on disk -- the media half of HomeCatalog's
    // ModelsPresent union (2.8). Carries both the specific id ("sdxl-base") and the broader capability ("image")
    // so a future card can gate on either "this exact model" or "any model of this capability".
    public IReadOnlyList<string> PresentTags() => Entries()
        .Where(e => File.Exists(PathFor(e)))
        .SelectMany(e => new[] { e.Id, e.Capability })
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string Install(string id)
    {
        var e = Entries().FirstOrDefault(x => x.Id == id);
        if (e is null) return "unknown";
        if (File.Exists(PathFor(e))) return "installed";
        if (_downloads.TryGetValue(id, out var cur) && cur.Status == "downloading") return "downloading";
        var st = new DlState();
        _downloads[id] = st;
        _ = Task.Run(() => DownloadAsync(e, st));
        return "started";
    }

    public bool Delete(string id)
    {
        var e = Entries().FirstOrDefault(x => x.Id == id);
        if (e is null) return false;
        try { var dest = PathFor(e); if (File.Exists(dest)) File.Delete(dest); _downloads.TryRemove(id, out _); return true; }
        catch { return false; }
    }

    private async Task DownloadAsync(CatalogEntry e, DlState st)
    {
        var dest = PathFor(e);
        var part = dest + ".part";
        var verify = !string.IsNullOrWhiteSpace(e.Sha256);
        if (!verify) Console.Error.WriteLine($"[ModelManager] '{e.Id}' has no catalog sha256 -- installing without integrity verification.");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var resp = await Http.GetAsync(e.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? (long)(e.SizeGb * 1_000_000_000);
            using var sha = verify ? SHA256.Create() : null;
            await using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var fs = File.Create(part))
            {
                var buf = new byte[1 << 20];
                long read = 0; int n;
                while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    sha?.TransformBlock(buf, 0, n, null, 0);
                    read += n;
                    if (total > 0) st.Progress = Math.Min(1.0, (double)read / total);
                }
            }
            if (sha is not null)
            {
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var actualSha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                var verifyError = VerifySha256(e.File, e.Sha256, actualSha);
                if (verifyError is not null)
                {
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    throw new InvalidOperationException(verifyError);
                }
            }
            File.Move(part, dest, true);
            st.Progress = 1; st.Status = "done";
        }
        catch (Exception ex)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            st.Status = "failed"; st.Message = ex.Message;
        }
    }
}
