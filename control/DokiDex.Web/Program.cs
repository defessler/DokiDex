using DokiDex.Control.Services;
using DokiDex.Web;

// DokiDex local web studio host. Single-user, loopback-only ASP.NET Core that reuses the existing
// control plane (DokiService -> StatusProbe/Lifecycle) as its API, serves the SPA, and (P1) bridges
// SwarmUI's generation WebSocket to the browser over SignalR.
// ContentRoot = the exe's own directory (not the launch CWD), so wwwroot resolves no matter what working
// directory the tray launches us from.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// Loopback-only bind. Port is fixed by default but overridable so the tray can pick a free one and
// pass it (--port=NNNN or DOKIDEX_WEB_PORT).
int port = 5111;
if (int.TryParse(Environment.GetEnvironmentVariable("DOKIDEX_WEB_PORT"), out var ep)) port = ep;
var argPort = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.Ordinal));
if (argPort is not null && int.TryParse(argPort["--port=".Length..], out var ap)) port = ap;
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));   // 127.0.0.1 + ::1 only

builder.Services.AddSignalR();
builder.Services.AddSingleton<DokiService>();
builder.Services.AddSingleton<GenerationJobs>();

var app = builder.Build();

// Defense-in-depth for an unauthenticated localhost host (loopback bind alone is NOT enough):
// Host-header allowlist (DNS-rebinding) + Origin check on state-changing verbs (CSRF).
app.UseMiddleware<LocalSecurityMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles();

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
// applies to the implicit auto-switch-on-generate path added in P1).
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

// ---- generation (P1a: tested CLI recipe path + a single-flight job queue; P1b adds the live WS bridge) ----
api.MapPost("/generate", (GenSubmit body, GenerationJobs jobs) =>
{
    if (string.IsNullOrWhiteSpace(body.Prompt)) return Results.BadRequest(new { error = "empty prompt" });
    var kind = (body.Kind ?? "image").Trim().ToLowerInvariant();
    if (Array.IndexOf(GenRequest.Kinds, kind) < 0) return Results.BadRequest(new { error = "unknown kind" });
    var req = new GenRequest(body.Prompt.Trim(), kind,
        Fast: body.Fast, Upscale: body.Upscale, Refine: body.Refine,
        Face: body.Face, Realism: body.Realism, Raw: body.Raw, InitImage: body.InitImage);
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
    var mime = Path.GetExtension(j.ArtifactPath!).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".flac" => "audio/flac",
        _ => "application/octet-stream",
    };
    return Results.File(j.ArtifactPath!, mime);   // scoped: only files the app generated for a known job id
});

app.MapHub<StudioHub>("/hub");
app.MapFallbackToFile("index.html");

app.Run();
