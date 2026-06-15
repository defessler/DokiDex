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

    public async Task<StatusDoc?> GetStatusAsync(CancellationToken ct = default)
    {
        var json = await CaptureAsync(new[] { "status", "json" }, ct).ConfigureAwait(false);
        return ParseStatus(json);
    }

    // Pure, testable: deserialize the `doki status json` payload. Returns null on empty/invalid.
    public static StatusDoc? ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StatusDoc>(json, JsonOpts); }
        catch { return null; }
    }

    // Control actions are fire-and-forget: doki runs in its own hidden pwsh and the 2s poll
    // reflects the new state. Mode switches can take a while (model loads) — we don't block.
    public void Up(string profile) => Spawn(new[] { "up", profile });
    public void Down() => Spawn(new[] { "down" });
    public void StartService(string svc) => Spawn(new[] { "start", svc });
    public void StopService(string svc) => Spawn(new[] { "stop", svc });
    public void RestartService(string svc) => Spawn(new[] { "restart", svc });

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
    private static readonly string GenTempDir = Path.Combine(Path.GetTempPath(), "dokigen");
    private static readonly string[] AllowedMedia = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm", ".mp3", ".wav", ".flac" };

    private static int _genSeq;   // process-wide monotonic suffix: makes the path unique even for two gens in the same ms

    // The panel owns the artifact's temp location (so it can both pass -Out and later scope-check the open).
    public string NewGenOutPath(string kind)
    {
        Directory.CreateDirectory(GenTempDir);
        var seq = Interlocked.Increment(ref _genSeq);
        return Path.Combine(GenTempDir, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}-{seq}{GenRequest.OutExtensionFor(kind)}");
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

    // Open a media artifact the panel itself generated. Scoped to GenTempDir + an allowlisted media
    // extension so this can never be coerced into shell-opening an arbitrary path (cf. OpenArtifact's note).
    public void OpenLocalMedia(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(GenTempDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
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

    private static void Spawn(string[] args)
    {
        var psi = NewPsi(args, capture: false);
        try { Process.Start(psi)?.Dispose(); } catch { }   // fire-and-forget: release the handle (no output read)
    }

    private static async Task<string> CaptureAsync(string[] args, CancellationToken ct)
    {
        var psi = NewPsi(args, capture: true);
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null) return "";
            // Drain BOTH streams. stderr is redirected, so leaving it unread would let a large
            // stderr write fill the OS pipe buffer, block the child, and deadlock WaitForExit —
            // hanging the status poll. The 30s cap guards against an otherwise-wedged pwsh.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return outTask.Result;
        }
        catch
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            return "";
        }
        finally { p?.Dispose(); }
    }

    // Like CaptureAsync but returns exit-success + BOTH streams, with a caller-set timeout (gen runs for
    // minutes, not the 30s status budget). Same pipe-drain discipline so a chatty child can't deadlock.
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
