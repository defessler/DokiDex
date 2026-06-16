using System.IO;
using DokiDex.Control.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DokiDex.Web;

// The DokiDex local web studio, defined ONCE and hosted two ways:
//   • in-process by the WPF panel (StudioLauncherWindow -> StartInProcessAsync), so a release ships ONE
//     self-contained exe — no second process to co-publish/find/kill; and
//   • by the thin standalone DokiDex.Web.exe (dev: `dotnet run --project control/DokiDex.Web`), for
//     iterating on the SPA without launching WPF.
// Single-user, loopback-only ASP.NET Core that reuses the existing control plane (DokiService) as its API,
// serves the embedded SPA, and bridges SwarmUI's generation WebSocket to the browser.
public static class StudioHost
{
    public const int DefaultPort = 5111;

    // Resolve the listen port: --port=NNNN (highest) > DOKIDEX_WEB_PORT env > default. Lets the panel pick a
    // free port and the dev exe override it without code changes.
    public static int ResolvePort(string[] args)
    {
        int port = DefaultPort;
        if (int.TryParse(Environment.GetEnvironmentVariable("DOKIDEX_WEB_PORT"), out var ep)) port = ep;
        var argPort = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.Ordinal));
        if (argPort is not null && int.TryParse(argPort["--port=".Length..], out var ap)) port = ap;
        return port;
    }

    // Build a fully-configured WebApplication bound to 127.0.0.1:port (loopback + ::1 only). ContentRoot is the
    // exe's own directory so nothing depends on the launch CWD; the SPA is served from an embedded resource so
    // a single-file exe carries it with no wwwroot on disk.
    public static WebApplication Build(int port, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));   // 127.0.0.1 + ::1 only

        builder.Services.AddSignalR();
        builder.Services.AddSingleton<DokiService>();
        builder.Services.AddSingleton<GenerationJobs>();
        builder.Services.AddSingleton<GalleryService>();
        builder.Services.AddSingleton<ModelManager>();

        var app = builder.Build();

        // Defense-in-depth for an unauthenticated localhost host (loopback bind alone is NOT enough):
        // Host-header allowlist (DNS-rebinding) + Origin check on state-changing verbs (CSRF).
        app.UseMiddleware<LocalSecurityMiddleware>();

        MapApi(app);
        app.MapHub<StudioHub>("/hub");
        // SPA fallback: any non-API/-hub route returns the embedded index.html (client-side routing).
        app.MapFallback(() => Results.Content(IndexHtml.Value, "text/html; charset=utf-8"));
        return app;
    }

    // Start the studio in-process (used by the WPF panel). Returns the running app; StopAsync/DisposeAsync it
    // to shut the server down. Kestrel runs on its own threads, so this does not block the UI thread.
    public static async Task<WebApplication> StartInProcessAsync(int port)
    {
        var app = Build(port);
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static void MapApi(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { ok = true }));

        api.MapGet("/status", async (DokiService doki, CancellationToken ct) =>
        {
            var doc = await doki.GetStatusAsync(ct);
            return doc is null
                ? Results.Json(new { error = "no-home", message = "DokiDex home not found (set InstallRoot or run from the repo)" }, statusCode: 503)
                : Results.Json(doc);
        });

        // Explicit mode switch from the dashboard = user intent, so it switches directly (the eviction-confirm
        // applies to the implicit auto-switch-on-generate path).
        api.MapPost("/mode/{profile}", (string profile, DokiService doki) =>
        {
            if (profile is not ("agent" or "coexist" or "media")) return Results.BadRequest(new { error = "unknown profile" });
            doki.Up(profile);
            return Results.Accepted();
        });

        api.MapPost("/down", (DokiService doki) => { doki.Down(); return Results.Accepted(); });

        api.MapPost("/services/{name}/{action}", (string name, string action, DokiService doki) =>
        {
            switch (action)
            {
                case "start": doki.StartService(name); break;
                case "stop": doki.StopService(name); break;
                case "restart": doki.RestartService(name); break;
                default: return Results.BadRequest(new { error = "unknown action" });
            }
            return Results.Accepted();
        });

        // ---- generation (tested CLI recipe path -BodyOnly + single-flight job queue + live WS bridge) ----
        api.MapPost("/generate", (GenSubmit body, GenerationJobs jobs) =>
        {
            if (string.IsNullOrWhiteSpace(body.Prompt)) return Results.BadRequest(new { error = "empty prompt" });
            var kind = (body.Kind ?? "image").Trim().ToLowerInvariant();
            if (Array.IndexOf(GenRequest.Kinds, kind) < 0) return Results.BadRequest(new { error = "unknown kind" });
            // Init/mask images arrive from the browser as data: URLs; the recipe wants file paths, so decode each
            // to a temp file (edit/i2v/img2img + inpaint). Non-data values pass through (a server-side path).
            static string? SaveDataUrl(string? v, string tag)
            {
                if (string.IsNullOrEmpty(v) || !v.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return v;
                try
                {
                    var comma = v.IndexOf(',');
                    var meta = comma > 5 ? v[5..comma] : "";
                    var ext = meta.Contains("png") ? ".png" : meta.Contains("webp") ? ".webp" : ".jpg";
                    var p = Path.Combine(Path.GetTempPath(), $"dokidex-{tag}-" + Guid.NewGuid().ToString("N") + ext);
                    File.WriteAllBytes(p, Convert.FromBase64String(v[(comma + 1)..]));
                    return p;
                }
                catch { return null; }
            }
            var initPath = SaveDataUrl(body.InitImage, "init");
            if (body.InitImage is not null && initPath is null) return Results.BadRequest(new { error = "bad init image" });
            var maskPath = SaveDataUrl(body.MaskImage, "mask");
            if (body.MaskImage is not null && maskPath is null) return Results.BadRequest(new { error = "bad mask image" });
            var req = new GenRequest(body.Prompt.Trim(), kind,
                Fast: body.Fast, Upscale: body.Upscale, Refine: body.Refine,
                Face: body.Face, Realism: body.Realism, Raw: body.Raw, InitImage: initPath,
                Seed: body.Seed, Count: Math.Clamp(body.Count, 1, 9), Strength: body.Strength, MaskImage: maskPath, Aspect: body.Aspect,
                Lyrics: body.Lyrics, Duration: body.Duration, Bpm: body.Bpm, Lora: body.Lora);
            return Results.Json(jobs.Submit(req).ToDto());
        });
        api.MapGet("/jobs", (GenerationJobs jobs) => Results.Json(jobs.Recent().Select(j => j.ToDto())));
        api.MapGet("/jobs/{id}", (string id, GenerationJobs jobs) =>
            jobs.Get(id) is { } j ? Results.Json(j.ToDto()) : Results.NotFound());
        api.MapPost("/jobs/{id}/cancel", async (string id, GenerationJobs jobs) => { await jobs.Cancel(id); return Results.Accepted(); });
        api.MapGet("/media/{id}", (string id, GenerationJobs jobs) =>
        {
            var j = jobs.Get(id);
            if (j is null || !j.HasArtifact) return Results.NotFound();
            return Results.File(j.ArtifactPath!, GalleryService.Mime(j.ArtifactPath!));   // scoped: only files the app generated for a known job id
        });

        // ---- library / gallery (persistent over the app-owned output folder + JSON sidecars) ----
        api.MapGet("/gallery", (GalleryService gal) => Results.Json(gal.List()));
        api.MapGet("/gallery/media/{name}", (string name, GalleryService gal) =>
        {
            var full = gal.Resolve(name);
            return full is null ? Results.NotFound() : Results.File(full, GalleryService.Mime(full));
        });
        api.MapDelete("/gallery/{name}", (string name, GalleryService gal) =>
            gal.Delete(name) ? Results.Ok() : Results.NotFound());

        // ---- model & workflow manager (capability catalog + presence + direct download + delete) ----
        api.MapGet("/models", (ModelManager mm) => Results.Json(mm.List()));
        api.MapGet("/loras", () => Results.Json(Loras.List()));   // for the LoRA mixer (image-family)
        api.MapPost("/models/{id}/install", (string id, ModelManager mm) => Results.Json(new { status = mm.Install(id) }));
        api.MapDelete("/models/{id}", (string id, ModelManager mm) => mm.Delete(id) ? Results.Ok() : Results.NotFound());

        // ---- script-to-shotlist director (local instruct model on :8080 -> ordered shot prompts) ----
        // Storyboarding is text-only and runs in agent/coexist mode; the user then generates the shots as images
        // in media mode (the shotlist survives the GPU switch). Returns a clean message when the LLM is down.
        api.MapPost("/director/shotlist", async (DirectorRequest body, CancellationToken ct) =>
        {
            var r = await Director.StoryboardAsync(body.Idea ?? "", body.Shots, ct);
            return r.Ok
                ? Results.Json(new { shots = r.Shots })
                : Results.Json(new { error = r.Message, shots = r.Shots }, statusCode: 503);
        });

        // ---- multi-character composer (base scene + isolated per-character regions -> one raw SwarmUI prompt) ----
        // Pure compile (no GPU); the SPA generates the result via /api/generate with raw=true so the <object:..>
        // regional tags reach SwarmUI unrewritten.
        api.MapPost("/compose/multichar", (MultiCharSpec body) => Results.Json(new { prompt = MultiCharacter.Compile(body) }));

        // ---- steerable rewriter (user-directed prompt rewrite via the local LLM; conversational iterate) ----
        api.MapPost("/rewrite", async (RewriteRequest body, CancellationToken ct) =>
        {
            var r = await Rewriter.RewriteAsync(body.Prompt ?? "", body.Instruction ?? "", ct);
            return r.Ok ? Results.Json(new { prompt = r.Prompt }) : Results.Json(new { error = r.Error }, statusCode: 503);
        });
    }

    // The SPA is embedded (LogicalName DokiDex.studio.index.html) so the single-file exe carries it with no
    // wwwroot on disk. Loaded once, lazily, from THIS assembly (Control) — the same bytes whether hosted
    // in-process or by the standalone exe (which references this assembly).
    private static readonly Lazy<string> IndexHtml = new(LoadIndex);
    private static string LoadIndex()
    {
        var asm = typeof(StudioHost).Assembly;
        using var s = asm.GetManifestResourceStream("DokiDex.studio.index.html");
        if (s is null) return "<!doctype html><meta charset=utf-8><title>DokiDex Studio</title><body style='font-family:sans-serif;background:#0A0E14;color:#E6EEF6;padding:2rem'>SPA resource missing from the build.</body>";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
