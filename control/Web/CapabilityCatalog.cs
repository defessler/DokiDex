using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// One kind entry from the kind catalog (from doki gen -ListKinds or the static fallback).
public sealed record KindEntry(string Id, string Label, string Group, bool Ready, string? Requires);

// Loads the kind catalog by shelling `doki gen x -ListKinds` (reusing DokiService's NewPsi/scan-stdout
// approach) and caches the result after first load. Falls back to a static list of all 12 kinds if the shell
// fails or returns garbage. The static fallback is the single source of truth for the C# side and is kept
// exactly in sync with Get-GenKindCatalog in serving/doki-gen.ps1 — enforced by KindSyncTests.
public static class CapabilityCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Static fallback: all 12 kinds in catalog order. Must mirror Get-GenKindCatalog in serving/doki-gen.ps1
    // and GenArgs.Kinds in Services/GenArgs.cs — the KindSyncTests sync test enforces this.
    public static readonly IReadOnlyList<KindEntry> StaticFallback = new[]
    {
        new KindEntry("image",        "image",          "image", true,  null),
        new KindEntry("video",        "video",          "video", true,  null),
        new KindEntry("music",        "music",          "audio", true,  null),
        new KindEntry("edit",         "edit",           "image", true,  null),
        new KindEntry("i2v",          "i2v",            "video", true,  null),
        new KindEntry("foley",        "foley",          "video", true,  null),
        new KindEntry("ltx",          "video + audio",  "video", true,  null),
        new KindEntry("faceid",       "face id",        "image", false, "setup.ps1 -FaceId"),
        new KindEntry("pulid",        "face id (flux)", "image", false, "setup.ps1 -Pulid"),
        new KindEntry("infinitetalk", "talking video",  "video", false, "setup.ps1 -InfiniteTalk"),
        new KindEntry("latentsync",   "lip sync",       "video", false, "setup.ps1 -LatentSync"),
        new KindEntry("speech",       "speech",         "audio", false, "setup.ps1 -TtsSuite"),
    };

    // Cache: null = not yet loaded; non-null = the loaded or fallback list.
    private static IReadOnlyList<KindEntry>? _cache;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    // All 12 kinds (ready and gated). Loads once, cached thereafter.
    public static async Task<IReadOnlyList<KindEntry>> GetKindsAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;
            _cache = await LoadFromShellAsync(ct).ConfigureAwait(false) ?? StaticFallback;
            return _cache;
        }
        catch
        {
            _cache ??= StaticFallback;
            return _cache;
        }
        finally { _lock.Release(); }
    }

    // Shell `doki gen x -ListKinds` using the same NewPsi/capture pattern as DokiService.GetGenBodyAsync.
    // Scans stdout bottom-to-top for the JSON array line (mirrors the -BodyOnly scan). Returns null on any
    // failure so the caller falls back to the static list.
    internal static async Task<IReadOnlyList<KindEntry>?> LoadFromShellAsync(CancellationToken ct = default)
    {
        if (!RepoPaths.HasValidRoot) return null;
        var psi = NewPsi(new[] { "gen", "x", "-ListKinds" });
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            if (p.ExitCode != 0) return null;
            return ParseJson(outTask.Result);
        }
        catch { return null; }
        finally { p?.Dispose(); }
    }

    // Parse the compact JSON array from -ListKinds stdout. Scans bottom-to-top for the JSON line.
    // Returns null on any parse failure so the caller falls back to the static list.
    internal static IReadOnlyList<KindEntry>? ParseJson(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var lines = stdout.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith('[')) continue;
            try
            {
                var entries = JsonSerializer.Deserialize<KindEntry[]>(t, JsonOpts);
                if (entries is { Length: > 0 }) return entries;
            }
            catch { }
        }
        return null;
    }

    // Reset the cache (test seam — allows unit tests to re-exercise the load path without restart).
    internal static void ResetCache() { _cache = null; }

    // Build a ProcessStartInfo that shells doki gen via pwsh — same plumbing as DokiService.NewPsi.
    private static ProcessStartInfo NewPsi(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
