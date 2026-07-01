using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using DokiDex.Control.Models;

namespace DokiDex.Control.Services;

// The panel's only path to the control plane: shells out to doki.ps1. doki stays
// authoritative (group exclusion, .run\* lifecycle) — the panel never re-implements it.
public sealed class DokiService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Status is NATIVE now (no pwsh on the hot path): StatusProbe probes health + pidfiles + nvidia-smi +
    // llama-swap directly and returns the same StatusDoc the panel already parses. Null only when the home is
    // missing/moved, so the overlay's "Locate DokiDex folder…" recovery still fires for that case.

    // SHORT-TTL cache: /api/home and /api/capabilities both call GetStatusAsync, so a page render fires two
    // back-to-back nvidia-smi subprocesses (300–800 ms each). ~1 s TTL collapses concurrent/rapid calls to
    // one probe. The TTL is short enough that status is never meaningfully stale.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);
    private StatusDoc? _cachedDoc;
    private DateTime _cacheTime;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    // Injectable seams: test overrides these to freeze the clock and count probes without hitting nvidia-smi.
    internal Func<DateTime> _now = () => DateTime.UtcNow;
    internal Func<CancellationToken, Task<StatusDoc>> _probe = StatusProbe.GetAsync;
    internal Func<bool> _hasRoot = () => RepoPaths.HasValidRoot;

    public async Task<StatusDoc?> GetStatusAsync(CancellationToken ct = default)
    {
        if (!_hasRoot()) return null;
        // Fast path: lock-free stale-read check. Reference reads are atomic in .NET; a torn _cacheTime
        // at worst causes one extra probe — acceptable for a 1 s TTL.
        if (_cachedDoc is { } hit && _now() - _cacheTime < CacheTtl) return hit;
        // Slow path: serialize concurrent callers so only one probe fires per TTL window.
        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedDoc is { } hit2 && _now() - _cacheTime < CacheTtl) return hit2;
            var result = await _probe(ct).ConfigureAwait(false);
            _cachedDoc = result; _cacheTime = _now();
            return result;
        }
        finally { _cacheLock.Release(); }
    }

    // Pure, testable: deserialize the `doki status json` payload. Returns null on empty/invalid.
    public static StatusDoc? ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StatusDoc>(json, JsonOpts); }
        catch { return null; }
    }

    // Control actions are native + fire-and-forget: Lifecycle evicts the opposite GPU group and shells the
    // bundled per-service launcher (detached); the 2s native status poll reflects the new state. We don't block.
    public void Up(string profile) => Lifecycle.Up(profile);
    public void Down() => Lifecycle.Down();
    public void StartService(string svc) => Lifecycle.Start(svc);
    public void StopService(string svc) => Lifecycle.Stop(svc);
    public void RestartService(string svc) => Lifecycle.Restart(svc);

    // Launch an http(s) URL only. UseShellExecute resolves the string against Windows protocol/file
    // associations, so anything non-http(s) (file://, UNC \\host\share, ms-msdt:/search-ms:/vscode: handlers)
    // could execute code or leak NTLM — reject all of it. Every service UI in $Services is an http loopback URL.
    public void OpenUi(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    // Open a generated artifact: an http(s) URL (a SwarmUI image) or a fully-qualified local file the panel
    // itself wrote (a TTS/STT temp .wav). Kept separate so the http(s) guard above needn't special-case files.
    public void OpenArtifact(string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        { OpenUi(pathOrUrl); return; }
        if (Path.IsPathFullyQualified(pathOrUrl) && File.Exists(pathOrUrl)
            && string.Equals(Path.GetExtension(pathOrUrl), ".wav", StringComparison.OrdinalIgnoreCase))
        { try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true })?.Dispose(); } catch { } }
    }

    // ---- DokiGen Studio: text->media via `doki gen` (needs media mode; live run verified in a card session) ----
    // Default output dir = a folder beside the LAUNCHED exe (RepoPaths.ExeDir is single-file-safe), NOT %TEMP%
    // and NOT the repo. The user can override it (persisted) via AppSettings.GenOutputDir; resolved fresh each
    // call so a pick mid-session takes effect with no restart. GenDirResolver is an overridable seam for tests.
    internal static string DefaultGenDir => Path.Combine(RepoPaths.ExeDir, "DokiGen");
    internal static Func<string> GenDirResolver { get; set; } =
        () => { var s = AppSettings.Load().GenOutputDir; return string.IsNullOrWhiteSpace(s) ? DefaultGenDir : s!; };
    public static string GenDir => GenDirResolver();

    private static readonly string[] AllowedMedia = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm", ".mp3", ".wav", ".flac" };
    private static int _genSeq;   // process-wide monotonic suffix: makes the path unique even for two gens in the same ms
    // Every -Out path the panel itself created. OpenLocalMedia opens ONLY these — the tightest possible scope,
    // and it survives an output-folder change mid-session (a single-dir prefix check would not).
    private static readonly HashSet<string> _generated = new(StringComparer.OrdinalIgnoreCase);

    // The panel owns the artifact's location (so it can both pass -Out and later scope-check the open). Writes
    // into the resolved output dir; if that isn't writable (e.g. the exe lives in Program Files) it falls back
    // to Pictures\DokiGen, then the temp dir — generation never fails for lack of a writable folder.
    public string NewGenOutPath(string kind)
    {
        var dir = EnsureWritableDir();
        var seq = Interlocked.Increment(ref _genSeq);
        var path = Path.Combine(dir, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}-{seq}{GenRequest.OutExtensionFor(kind)}");
        lock (_generated) _generated.Add(Path.GetFullPath(path));
        return path;
    }

    // The resolved output dir if creatable, else Pictures\DokiGen, else %TEMP%\dokigen — always returns a dir
    // that exists. The fallback is mandatory so a read-only exe location can't break generation.
    private static string EnsureWritableDir()
    {
        foreach (var dir in new[] {
            GenDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DokiGen"),
            Path.Combine(Path.GetTempPath(), "dokigen") })
        {
            try { Directory.CreateDirectory(dir); return dir; } catch { }
        }
        return Path.GetTempPath();
    }

    // Shell `doki gen …` and wait (gen can take minutes). doki saves the artifact to req.OutPath; success =
    // exit 0 AND the file landed. On failure, surface doki's last stderr/stdout line. The arg-building
    // (GenCli.BuildArgs) is pure + unit-tested; this is the thin live shell over it.
    public async Task<GenResult> RunGenAsync(GenRequest req, CancellationToken ct = default)
    {
        var args = GenCli.BuildArgs(req);
        var (ok, stdout, stderr) = await CaptureFullAsync(args, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (ok && !string.IsNullOrEmpty(req.OutPath) && File.Exists(req.OutPath))
            return new GenResult(true, req.OutPath, "done");
        var msg = LastMeaningfulLine(stderr) ?? LastMeaningfulLine(stdout) ?? "generation failed";
        return new GenResult(false, req.OutPath, msg);
    }

    // Build the SwarmUI GenerateText2Image body for a request WITHOUT generating (`doki gen … -BodyOnly`), so
    // the web host can drive GenerateText2ImageWS itself for live progress while the recipe stays single-sourced
    // in doki-gen.ps1. Returns the compact body JSON, or null if the request is invalid. Does NOT need media mode.
    public async Task<string?> GetGenBodyAsync(GenRequest req, CancellationToken ct = default)
    {
        var args = new List<string>(GenCli.BuildArgs(req)) { "-BodyOnly" };
        var (ok, stdout, _) = await CaptureFullAsync(args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!ok) return null;
        // scan bottom-to-top for the JSON line BodyOnly prints (explicit reverse loop — avoids the ambiguous
        // array .Reverse() that can bind to the void Span overload under some SDKs / Release publish).
        var lines = stdout.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        { var t = lines[i].Trim(); if (t.StartsWith('{')) return t; }
        return null;
    }

    // Open a media artifact the panel itself generated. Scoped to the set of -Out paths THIS panel created
    // (+ an allowlisted media extension) so it can never be coerced into shell-opening an arbitrary path, and
    // it keeps working after the output folder is changed (cf. OpenArtifact's note).
    public void OpenLocalMedia(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return;
        var full = Path.GetFullPath(path);
        bool ours; lock (_generated) ours = _generated.Contains(full);
        if (!ours) return;
        if (Array.IndexOf(AllowedMedia, Path.GetExtension(full).ToLowerInvariant()) < 0) return;
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    // last non-blank line, trimmed — doki throws (e.g. "SwarmUI not reachable …") land on stderr.
    internal static string? LastMeaningfulLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var lines = s.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (t.Length > 0) return t;
        }
        return null;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };

    // Warm-load a coder model into llama-swap by sending it a 1-token request — llama-swap
    // hot-swaps to it. Fire-and-forget; the 2s status poll reflects the new loaded model.
    public void WarmLoadModel(string model)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var body = new { model, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 1, temperature = 0 };
                await Http.PostAsJsonAsync("http://127.0.0.1:8080/v1/chat/completions", body);
            }
            catch { }
        });
    }

    public void RunVerifyConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo("pwsh",
                $"-NoProfile -NoExit -File \"{RepoPaths.DokiPs1}\" verify")
            { UseShellExecute = true, WorkingDirectory = RepoPaths.Root })?.Dispose();
        }
        catch { }
    }

    // Capture exit-success + BOTH streams with a caller-set timeout (gen runs for minutes). Drains both pipes
    // so a chatty child can't fill the OS pipe buffer and deadlock WaitForExit.
    private static async Task<(bool ok, string stdout, string stderr)> CaptureFullAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = NewPsi(args, capture: true);
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null) return (false, "", "could not start pwsh");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return (p.ExitCode == 0, outTask.Result, errTask.Result);
        }
        catch (OperationCanceledException)
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            return (false, "", "generation timed out");
        }
        catch (Exception ex)
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            return (false, "", ex.Message);
        }
        finally { p?.Dispose(); }
    }

    private static ProcessStartInfo NewPsi(IReadOnlyList<string> args, bool capture)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            WorkingDirectory = RepoPaths.Root,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(RepoPaths.DokiPs1);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
