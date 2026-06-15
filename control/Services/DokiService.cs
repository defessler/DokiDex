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

    private static ProcessStartInfo NewPsi(string[] args, bool capture)
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
