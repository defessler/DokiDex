using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// LLM/GGUF model manager for the local llama.cpp/llama-swap stack. Mirrors ModelManager's shape (catalog load,
// status listing, download -> .part -> move, delete, polled-progress endpoints) but every catalog entry carries
// an ARRAY of files (multi-part GGUFs: gpt-oss-120b = 3 parts, each with its own sha256; vision = model + mmproj)
// so every operation below is per-part. Reads media-assets/llm-model-catalog.json.
// G1 discipline: a human independently re-verifies every url+sha256 against the live HF LFS pointer before an
// entry is installable; an entry whose sha256 is still the literal "UNVERIFIED" is refused outright (never
// downloaded, let alone promoted) until that review lands. Never promote unverified bytes: byte-count and
// SHA-256 are checked on the *.part* file before it is moved into place; any mismatch deletes the .part.
public sealed class LlmModelManager
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };  // multi-GB files
    private readonly ConcurrentDictionary<string, EntryProgress> _progress = new();
    private readonly string _catalogPath;
    private readonly string _modelsRoot;
    private List<CatalogEntry>? _catalog;
    private string? _catalogError;

    public LlmModelManager()
        : this(Path.Combine(RepoPaths.Root, "media-assets", "llm-model-catalog.json"), RepoPaths.Root) { }

    // Test seam: point at a fixture catalog file + a scratch "models root" instead of the real repo, so tests
    // never touch the real catalog or the network.
    internal LlmModelManager(string catalogPath, string modelsRoot)
    {
        _catalogPath = catalogPath;
        _modelsRoot = modelsRoot;
    }

    public sealed class CatalogFile
    {
        public string File { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
    }

    public sealed class CatalogEntry
    {
        public string Id { get; set; } = "";
        public string Role { get; set; } = "";
        public string Label { get; set; } = "";
        public List<CatalogFile> Files { get; set; } = new();
        public double SizeGb { get; set; }
        public string? LlamaSwapModel { get; set; }
        public string? Notes { get; set; }
    }

    private sealed record Catalog(List<CatalogEntry> Models);

    private sealed class EntryProgress { public double Progress; public string Status = "downloading"; public string? Message; }

    // List() output shapes -- strongly typed (rather than anonymous) so unit tests can assert on them directly
    // and so the future UI leaf has a stable contract; serializes to the same JSON shape either way.
    public sealed record FileStatus(string File, bool Present);
    public sealed record ModelStatus(
        string Id, string Role, string Label, double SizeGb, string? LlamaSwapModel, string? Notes,
        string Status, List<FileStatus> Files, bool Downloading, double Progress, string? Error);
    public sealed record ModelsResult(List<ModelStatus> Models, string? Message);

    // -----------------------------------------------------------------------------------------------------
    // Pure seams -- no I/O, no network. Unit-tested directly with fixture strings/values.
    // -----------------------------------------------------------------------------------------------------

    // Catalog parse: json text -> entries. Lets a malformed/missing file degrade to an empty list + message
    // (see LoadCatalog) without ever throwing across the endpoint boundary.
    internal static List<CatalogEntry> ParseCatalog(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var c = JsonSerializer.Deserialize<Catalog>(json, opts);
        return c?.Models ?? new List<CatalogEntry>();
    }

    // Status decision: per-file exists booleans -> "present" (all), "partial" (some), "missing" (none, or a
    // file-less entry).
    internal static string StatusFromPresence(IReadOnlyList<bool> filesPresent)
    {
        if (filesPresent.Count == 0) return "missing";
        if (filesPresent.All(p => p)) return "present";
        if (filesPresent.Any(p => p)) return "partial";
        return "missing";
    }

    // Verify decision for one downloaded part: expected/actual sha+size -> null (ok) or a clear error naming
    // the file. Checked in order: (1) the catalog entry itself is UNVERIFIED -- refuse regardless of what was
    // downloaded (also used as a pre-download gate, see InstallAsync); (2) byte count; (3) case-insensitive
    // sha256. Never returns a partial pass -- any failure is a hard stop for that file.
    internal static string? VerifyDecision(string fileName, string expectedSha256, string actualSha256, long expectedSize, long actualSize)
    {
        if (string.Equals(expectedSha256, "UNVERIFIED", StringComparison.OrdinalIgnoreCase))
            return $"{fileName}: catalog entry is not yet human-verified (sha256=UNVERIFIED) -- refusing to install until G1 review lands.";
        if (actualSize != expectedSize)
            return $"{fileName}: downloaded {actualSize} bytes, expected {expectedSize} -- deleted the partial download.";
        if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            return $"{fileName}: sha256 mismatch (expected {expectedSha256}, got {actualSha256}) -- deleted the download.";
        return null;
    }

    // Disk-space decision (mirrors InstallPlan.FitsFreeSpace's shape): does the drive have room for the parts
    // still needed?
    internal static bool FitsFreeSpace(long neededBytes, long availableBytes) => availableBytes >= neededBytes;

    // -----------------------------------------------------------------------------------------------------
    // Catalog load + path helpers.
    // -----------------------------------------------------------------------------------------------------

    private (List<CatalogEntry> Entries, string? Message) LoadCatalog()
    {
        if (_catalog is not null) return (_catalog, _catalogError);
        try
        {
            if (!File.Exists(_catalogPath))
            {
                _catalog = new List<CatalogEntry>();
                _catalogError = "LLM model catalog not found.";
            }
            else
            {
                _catalog = ParseCatalog(File.ReadAllText(_catalogPath));
                _catalogError = null;
            }
        }
        catch (Exception ex)
        {
            _catalog = new List<CatalogEntry>();
            _catalogError = $"Failed to read LLM model catalog: {ex.Message}";
        }
        return (_catalog, _catalogError);
    }

    private string PathFor(CatalogFile f) => Path.Combine(_modelsRoot, f.File.Replace('/', Path.DirectorySeparatorChar));

    private static bool ExactlyPresent(string path, long size)
    {
        if (!File.Exists(path)) return false;
        try { return new FileInfo(path).Length == size; } catch { return false; }
    }

    private static long? FreeSpaceFor(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }   // degrade gracefully -- an unreadable drive root just skips the pre-check
    }

    private static string Human(long bytes) => $"{bytes / 1_000_000_000.0:0.00} GB";

    // -----------------------------------------------------------------------------------------------------
    // Public surface (endpoint-facing). Never throws -- IO/parse errors degrade to per-entry error strings.
    // -----------------------------------------------------------------------------------------------------

    public ModelsResult List()
    {
        var (entries, message) = LoadCatalog();
        var models = entries.Select(e =>
        {
            var filesPresent = e.Files.Select(f => File.Exists(PathFor(f))).ToList();
            _progress.TryGetValue(e.Id, out var st);
            return new ModelStatus(
                Id: e.Id,
                Role: e.Role,
                Label: e.Label,
                SizeGb: e.SizeGb,
                LlamaSwapModel: e.LlamaSwapModel,
                Notes: e.Notes,
                Status: StatusFromPresence(filesPresent),
                Files: e.Files.Select((f, i) => new FileStatus(f.File, filesPresent[i])).ToList(),
                Downloading: st is { Status: "downloading" },
                Progress: st?.Progress ?? 0,
                Error: st is { Status: "failed" } ? st.Message : null);
        }).ToList();
        return new ModelsResult(models, message);
    }

    // Fire-and-forget install, mirroring ModelManager.Install's contract exactly (so the future UI leaf can
    // reuse the same poll-the-list pattern): returns a status synchronously and kicks off the real work in the
    // background; progress/errors surface through List()'s per-entry downloading/progress/error fields.
    public string Install(string id)
    {
        var (entries, message) = LoadCatalog();
        if (message is not null) return "unknown";
        var e = entries.FirstOrDefault(x => x.Id == id);
        if (e is null) return "unknown";
        if (e.Files.Count > 0 && e.Files.All(f => ExactlyPresent(PathFor(f), f.Size))) return "installed";
        if (_progress.TryGetValue(id, out var cur) && cur.Status == "downloading") return "downloading";
        _progress[id] = new EntryProgress();
        _ = Task.Run(() => InstallAsync(id, CancellationToken.None));
        return "started";
    }

    public async Task InstallAsync(string id, CancellationToken ct)
    {
        var (entries, _) = LoadCatalog();
        var e = entries.FirstOrDefault(x => x.Id == id);
        var st = _progress.GetOrAdd(id, _ => new EntryProgress());
        if (e is null) { st.Status = "failed"; st.Message = "unknown model id"; return; }
        st.Status = "downloading"; st.Progress = 0; st.Message = null;
        try
        {
            // Refuse the whole entry outright if any part isn't G1-verified yet -- never spend bandwidth
            // downloading bytes we won't be allowed to promote. (VerifyDecision's UNVERIFIED branch short-
            // circuits before looking at actual size/sha, so the dummy actual values below are never consulted.)
            foreach (var f in e.Files)
            {
                if (string.Equals(f.Sha256, "UNVERIFIED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(VerifyDecision(f.File, f.Sha256, actualSha256: "", expectedSize: f.Size, actualSize: 0));
            }

            var remaining = e.Files.Where(f => !ExactlyPresent(PathFor(f), f.Size)).ToList();
            var neededBytes = remaining.Sum(f => f.Size);
            if (neededBytes > 0)
            {
                var free = FreeSpaceFor(_modelsRoot);
                if (free is long avail && !FitsFreeSpace(neededBytes, avail))
                    throw new InvalidOperationException($"Not enough disk space for '{id}': need {Human(neededBytes)}, {Human(avail)} free.");
            }

            var totalBytes = e.Files.Sum(f => f.Size);
            long bytesDoneBase = totalBytes - neededBytes;   // already-present parts count toward progress

            foreach (var f in e.Files)
            {
                var dest = PathFor(f);
                if (ExactlyPresent(dest, f.Size)) continue;   // already present w/ exact size -- skip re-download
                var part = dest + ".part";
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                using var sha = SHA256.Create();
                long fileBytesRead = 0;
                using (var resp = await Http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fs = File.Create(part);
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        fileBytesRead += n;
                        if (totalBytes > 0) st.Progress = Math.Min(1.0, (double)(bytesDoneBase + fileBytesRead) / totalBytes);
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var actualSha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

                var verifyError = VerifyDecision(f.File, f.Sha256, actualSha, f.Size, fileBytesRead);
                if (verifyError is not null)
                {
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    throw new InvalidOperationException(verifyError);
                }

                File.Move(part, dest, true);
                bytesDoneBase += f.Size;
            }

            st.Status = "done"; st.Progress = 1;
        }
        catch (Exception ex)
        {
            st.Status = "failed"; st.Message = ex.Message;
        }
    }

    public bool Delete(string id)
    {
        var (entries, _) = LoadCatalog();
        var e = entries.FirstOrDefault(x => x.Id == id);
        if (e is null) return false;
        try
        {
            foreach (var f in e.Files)
            {
                var p = PathFor(f);
                if (File.Exists(p)) File.Delete(p);
            }
            _progress.TryRemove(id, out _);
            return true;
        }
        catch { return false; }
    }
}
