using System.IO;
using System.Text.Json;
using DokiDex.Control.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
        api.MapPost("/generate", (GenSubmit body, GenerationJobs jobs, ModelManager mm) =>
        {
            if (string.IsNullOrWhiteSpace(body.Prompt)) return Results.BadRequest(new { error = "empty prompt" });
            var kind = (body.Kind ?? "image").Trim().ToLowerInvariant();
            if (Array.IndexOf(GenRequest.Kinds, kind) < 0) return Results.BadRequest(new { error = "unknown kind" });
            // checkpoint override: "auto" routes (prompt-aware pick among installed image bases); else pass through.
            var model = body.Model;
            if (string.Equals(model, "auto", StringComparison.OrdinalIgnoreCase))
                model = ModelRouter.Pick(body.Prompt, mm.InstalledImageModels())?.File;
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
            // ControlNet units: decode each unit's data-URL control image to a temp file (the recipe wants paths).
            List<ControlUnit>? controlUnits = null;
            if (body.ControlNets is { Count: > 0 })
            {
                controlUnits = new();
                foreach (var u in body.ControlNets)
                {
                    if (string.IsNullOrWhiteSpace(u.Model)) continue;
                    var img = SaveDataUrl(u.Image, "control");
                    if (u.Image is not null && img is null) return Results.BadRequest(new { error = "bad control image" });
                    controlUnits.Add(u with { Image = img });
                }
            }
            var endPath = SaveDataUrl(body.EndImage, "end");
            if (body.EndImage is not null && endPath is null) return Results.BadRequest(new { error = "bad end image" });
            var req = new GenRequest(body.Prompt.Trim(), kind,
                Fast: body.Fast, Quality: body.Quality, Upscale: body.Upscale, Refine: body.Refine,
                Face: body.Face, Realism: body.Realism, Raw: body.Raw, InitImage: initPath,
                Seed: body.Seed, Count: Math.Clamp(body.Count, 1, 9), Strength: body.Strength, MaskImage: maskPath, Aspect: body.Aspect,
                Lyrics: body.Lyrics, Duration: body.Duration, Bpm: body.Bpm, Lora: body.Lora, Negative: body.Negative,
                Upscaler: body.Upscaler, Segment: body.Segment,
                ControlNets: controlUnits,
                EndImage: endPath, Reference: body.Reference, RefWeight: body.RefWeight,
                Interpolate: body.Interpolate, InterpolateMult: body.InterpolateMult, Workflow: body.Workflow, Tile: body.Tile,
                Model: model, Ephemeral: body.Ephemeral);
            return Results.Json(jobs.Submit(req).ToDto());
        });
        api.MapGet("/jobs", (GenerationJobs jobs) => Results.Json(jobs.Recent().Select(j => j.ToDto())));
        api.MapGet("/jobs/{id}", (string id, GenerationJobs jobs) =>
            jobs.Get(id) is { } j ? Results.Json(j.ToDto()) : Results.NotFound());
        api.MapPost("/jobs/{id}/cancel", async (string id, GenerationJobs jobs) => { await jobs.Cancel(id); return Results.Accepted(); });
        // refine-from-result: re-run a finished card's image as img2img with one flag (face / hires / upscale)
        api.MapPost("/jobs/{id}/refine", (string id, RefineRequest body, GenerationJobs jobs) =>
        {
            var j = jobs.Get(id);
            if (j is null || !j.HasArtifact) return Results.NotFound();
            var req = Refine.Build(j.Prompt, j.ArtifactPath!, body.Action);
            return req is null ? Results.BadRequest(new { error = "unknown action" }) : Results.Json(jobs.Submit(req, Path.GetFileName(j.ArtifactPath)).ToDto());
        });
        // one-click effect presets: list + apply a stylistic transform to a finished card (img2img)
        api.MapGet("/effects", () => Results.Json(EffectPresets.All()));
        api.MapPost("/jobs/{id}/effect", (string id, RefineRequest body, GenerationJobs jobs) =>
        {
            var j = jobs.Get(id);
            if (j is null || !j.HasArtifact) return Results.NotFound();
            var preset = EffectPresets.Find(body.Action);   // RefineRequest.Action carries the preset id
            return preset is null ? Results.BadRequest(new { error = "unknown effect" })
                : Results.Json(jobs.Submit(EffectPresets.Build(preset, j.ArtifactPath!), Path.GetFileName(j.ArtifactPath)).ToDto());
        });
        api.MapGet("/media/{id}", (string id, GenerationJobs jobs) =>
        {
            var j = jobs.Get(id);
            if (j is null || !j.HasArtifact) return Results.NotFound();
            return Results.File(j.ArtifactPath!, GalleryService.Mime(j.ArtifactPath!));   // scoped: only files the app generated for a known job id
        });

        // ---- library / gallery (persistent over the app-owned output folder + JSON sidecars) ----
        api.MapGet("/gallery", (GalleryService gal, string? q, string? kind, string? view) => Results.Json(gal.List(q, kind, view)));
        api.MapGet("/gallery/media/{name}", (string name, GalleryService gal) =>
        {
            var full = gal.Resolve(name);
            return full is null ? Results.NotFound() : Results.File(full, GalleryService.Mime(full));
        });
        // keyboard-triage curation: flip favorite/trash on a card (F/X/U keys in the Library grid)
        api.MapPost("/gallery/{name}/rate", (string name, RateRequest body, GalleryService gal) =>
        {
            var s = gal.Rate(name, body.Favorite, body.Trash);
            return s is null ? Results.NotFound() : Results.Json(new { favorite = s.Favorite, trash = s.Trash });
        });
        api.MapDelete("/gallery/{name}", (string name, GalleryService gal) =>
            gal.Delete(name) ? Results.Ok() : Results.NotFound());
        // variation lineage: the forest of generations linked by their derived-from (Parent) sidecar field
        api.MapGet("/lineage", (GalleryService gal) => Results.Json(Lineage.BuildForest(gal.LineageItems())));
        // saved searches: named, re-applicable Library filters (re-evaluate over the live gallery)
        api.MapGet("/searches", () => Results.Json(SavedSearches.List()));
        api.MapPost("/searches", (SavedSearch body) => SavedSearches.Save(body) ? Results.Ok() : Results.BadRequest(new { error = "bad name" }));
        api.MapDelete("/searches/{name}", (string name) => SavedSearches.Delete(name) ? Results.Ok() : Results.NotFound());
        // one-click story-bible / pitch deck: selected (or recent) images + LLM logline/synopsis + @-ref cast,
        // laid out as one self-contained HTML file (LLM-gated prose degrades to image-only). Returns a download.
        api.MapPost("/pitchdeck", async (PitchDeckRequest body, GalleryService gal, CancellationToken ct) =>
        {
            var names = body.Names is { Count: > 0 }
                ? body.Names
                : gal.LineageItems().Where(i => i.Kind is "image" or "edit").Take(12).Select(i => i.Name).ToList();
            var scenes = new List<DeckScene>();
            foreach (var n in names.Take(16))
            {
                var url = gal.ImageDataUrl(n);
                if (url is null) continue;
                var meta = gal.Read(n);
                scenes.Add(new DeckScene(meta?.Prompt ?? "", url, meta?.Kind ?? "image"));
            }
            if (scenes.Count == 0) return Results.Json(new { error = "no images in the library yet" }, statusCode: 400);
            var cast = References.Entries().Select(r => new DeckCast(r.Name, r.Text)).ToList();
            var deck = await PitchDeck.ComposeAsync(body.Title, scenes, cast, ct, LlmTiers.Resolve(body.Tier));
            var html = System.Text.Encoding.UTF8.GetBytes(PitchDeck.BuildHtml(deck));
            return Results.File(html, "text/html; charset=utf-8", "pitch-deck.html");
        });

        // ---- deterministic, model-free color tools (browser ships raw RGBA; C# does the LAB math) ----
        api.MapPost("/palette", async (HttpRequest req) =>
        {
            int k = int.TryParse(req.Query["k"], out var kk) ? kk : 6;
            using var ms = new MemoryStream(); await req.Body.CopyToAsync(ms);
            var colors = ColorTools.DominantColors(ms.ToArray(), k).Select(ColorTools.ToHex).ToList();
            return Results.Json(new { colors });
        });
        api.MapPost("/recolor", async (HttpRequest req) =>
        {
            var palette = ColorTools.ParsePalette(req.Query["palette"]);
            using var ms = new MemoryStream(); await req.Body.CopyToAsync(ms);
            try { return Results.Bytes(ColorTools.RemapToPalette(ms.ToArray(), palette), "application/octet-stream"); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "bad pixel buffer" }); }
        });
        // import a browser-produced PNG (a recolor/canvas result) into the Library; name is server-generated
        // (NewGenOutPath) so there's no client path => no traversal. Optional prompt/parent feed search + lineage.
        api.MapPost("/import", async (HttpRequest req, DokiService doki) =>
        {
            using var ms = new MemoryStream(); await req.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return Results.BadRequest(new { error = "empty image" });
            var outPath = doki.NewGenOutPath("image");
            try
            {
                await File.WriteAllBytesAsync(outPath, bytes);
                var prompt = req.Query["prompt"].ToString();
                var parent = req.Query["parent"].ToString();
                GalleryService.WriteSidecar(outPath, "import", "image", prompt, string.IsNullOrEmpty(parent) ? null : parent);
                var name = Path.GetFileName(outPath);
                return Results.Json(new { name, mediaUrl = $"/api/gallery/media/{Uri.EscapeDataString(name)}" });
            }
            catch { return Results.Json(new { error = "could not save" }, statusCode: 500); }
        });

        // ---- model & workflow manager (capability catalog + presence + direct download + delete) ----
        api.MapGet("/models", (ModelManager mm) => Results.Json(mm.List()));
        api.MapGet("/image-models", (ModelManager mm) => Results.Json(mm.InstalledImageModels()));   // manual picker + Auto router
        api.MapGet("/loras", () => Results.Json(Loras.List()));   // for the LoRA mixer (image-family)
        api.MapGet("/controlnet-models", () => Results.Json(Loras.ControlNets()));   // for the ControlNet model picker
        api.MapPost("/models/{id}/install", (string id, ModelManager mm) => Results.Json(new { status = mm.Install(id) }));
        api.MapDelete("/models/{id}", (string id, ModelManager mm) => mm.Delete(id) ? Results.Ok() : Results.NotFound());

        // ---- script-to-shotlist director (local instruct model on :8080 -> ordered shot prompts) ----
        // Storyboarding is text-only and runs in agent/coexist mode; the user then generates the shots as images
        // in media mode (the shotlist survives the GPU switch). Returns a clean message when the LLM is down.
        api.MapPost("/director/shotlist", async (DirectorRequest body, CancellationToken ct) =>
        {
            var r = await Director.StoryboardAsync(body.Idea ?? "", body.Shots, ct, LlmTiers.Resolve(body.Tier));
            return r.Ok
                ? Results.Json(new { shots = r.Shots })
                : Results.Json(new { error = r.Message, shots = r.Shots }, statusCode: 503);
        });

        // ---- persona chat (P0, non-streaming): persistent multi-turn chat over a persona card on the local ----
        // instruct model (:8080, agent/coexist mode). The server persists history in ChatStore, so the SPA need
        // not resend the transcript. Mirrors the Director 503 "start agent mode first" contract verbatim: when
        // the LLM is down the request returns 503 + the canonical message (same as Director at the shotlist endpoint).
        // When body.Tools is true the turn runs through the bounded TOOL-CALLING agent loop (Chat.AgentAsync) with
        // the curated single-tool registry (search_library) and returns the tool steps taken; Tools=false (the
        // default) keeps the exact current Chat.SendAsync path. Same 503 + canonical-string contract either way.
        api.MapPost("/chat", async (ChatRequest body, CancellationToken ct) =>
        {
            var r = body.Tools
                ? await Chat.AgentAsync(body, LlmTiers.Resolve(body.Tier), ct)
                : await Chat.SendAsync(body, LlmTiers.Resolve(body.Tier), ct);
            if (!r.Ok)
                return Results.Json(new { error = r.Message }, statusCode: 503);   // canonical "start agent mode first"
            // Only the tools path carries steps; the plain send omits the field entirely (no "steps":null noise).
            return r.Steps is { Count: > 0 }
                ? Results.Json(new { conversation = r.ConversationId, text = r.Text, steps = r.Steps })
                : Results.Json(new { conversation = r.ConversationId, text = r.Text });
        });

        // ---- persona chat (P2, streaming): the streaming twin of POST /api/chat over SSE on the POST response ----
        // body (the SPA reads res.body.getReader(); no EventSource, no SignalR client). Once the headers flush
        // (HTTP 200) we CANNOT return a 503, so the LLM-down case is surfaced as an in-band 'event: error' frame
        // with the canonical "start agent mode first" string; the non-streaming /api/chat above keeps the real 503
        // (it is the P2 fallback). Each token delta is JSON-wrapped ('data: {"t":...}') so newlines/quotes in a
        // token survive SSE framing; the SPA JSON.parses each data frame. Bounded by ctx.RequestAborted.
        api.MapPost("/chat/stream", async (ChatRequest body, HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var ct = ctx.RequestAborted;
            var any = false;
            await foreach (var ev in Chat.StreamAsync(body, LlmTiers.Resolve(body.Tier), ct))
            {
                if (ev.IsMeta)
                {
                    // Leading meta frame: hand the conversation id to the SPA before any token arrives.
                    var meta = JsonSerializer.Serialize(new { conversation = ev.ConversationId ?? "" });
                    await ctx.Response.WriteAsync($"event: meta\ndata: {meta}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    continue;
                }
                if (ev.Delta is { Length: > 0 })
                {
                    any = true;
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { t = ev.Delta })}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }

            // Zero deltas (LLM down / no model loaded) — can't 503 after headers; emit the canonical in-band error.
            if (!any)
                await ctx.Response.WriteAsync(
                    "event: error\ndata: LLM not reachable at :8080 — start agent mode first\n\n", ct);

            await ctx.Response.WriteAsync("event: done\ndata: end\n\n", ct);
        });

        // ---- persona character cards (the GPTs analog, local + uncensored) — clone of /api/references ----
        api.MapGet("/personas", () => Results.Json(Persona.List()));
        api.MapGet("/personas/{name}", (string name) =>
            Persona.Load(name) is { } card ? Results.Json(card) : Results.NotFound());
        api.MapPost("/personas", (PersonaCard body) =>
            Persona.Save(body) ? Results.Ok() : Results.BadRequest(new { error = "bad persona name" }));
        api.MapDelete("/personas/{name}", (string name) => Persona.Delete(name) ? Results.Ok() : Results.NotFound());

        // ---- lorebooks ("World Info" keyword-triggered context injection) — clone of /api/recipes ----
        api.MapGet("/lorebooks", () => Results.Json(Lorebook.List()));
        api.MapGet("/lorebooks/{name}", (string name) =>
            Lorebook.Load(name) is { } book ? Results.Json(book) : Results.NotFound());
        api.MapPost("/lorebooks", (LoreBook body) =>
            Lorebook.Save(body) ? Results.Ok() : Results.BadRequest(new { error = "bad lorebook name" }));
        api.MapDelete("/lorebooks/{name}", (string name) => Lorebook.Delete(name) ? Results.Ok() : Results.NotFound());

        // ---- persisted conversations (server-generated id => no client path => no traversal) — clone of /api/searches ----
        api.MapGet("/chats", () => Results.Json(ChatStore.List()));
        api.MapGet("/chats/{id}", (string id) =>
            ChatStore.Load(id) is { } conv ? Results.Json(conv) : Results.NotFound());
        // Deleting a conversation also drops its KB chunks from doc_index.db so a deleted-with-docs thread's
        // vectors don't accumulate forever (the disk-leak fix). BEST-EFFORT: the cleanup runs AFTER the thread
        // file is removed and can NEVER fail the delete — a down embed/index just leaves the rows for a later
        // reset. KbId == the conversation id in the first slice; we read the stored KbId before deleting so a
        // later named-KB slice still cleans the right scope.
        api.MapDelete("/chats/{id}", async (string id, CancellationToken ct) =>
        {
            var conv = ChatStore.Load(id);
            if (conv is null) return Results.NotFound();
            var kbId = string.IsNullOrWhiteSpace(conv.KbId) ? id : conv.KbId!;
            if (!ChatStore.Delete(id)) return Results.NotFound();
            try { await DocSearch.DeleteAsync(kbId, ct); } catch { /* never fail the delete on a KB-cleanup hiccup */ }
            return Results.Ok();
        });

        // ---- per-conversation KNOWLEDGE BASE (RAG "chat with your documents"): attach/list/remove plain-text docs
        // (.txt/.md, first slice) on a conversation. The KB id IS the conversation id (a doc attaches to a thread,
        // not globally); ingest chunks+embeds via the :8090 embed server and stores in doc_index.db scoped by that
        // id. RetrieveDocs then injects the top-K relevant chunks into ChatPrompt.Build each turn. Degrades like
        // code_search: a down embed server makes ingest return Ok=false (surfaced here) and retrieval inject nothing
        // (plain chat unchanged). The doc TEXT is sent in the JSON body (the browser reads the file client-side).
        api.MapPost("/chats/{id}/docs", async (string id, DocAttachRequest body, HttpRequest req, CancellationToken ct) =>
        {
            // Reject a multi-MB body up front (Content-Length) so an oversized paste fails with a clean, clear
            // error instead of buffering + timing the embed loop into a misleading 503. The char-level cap below
            // (DocSearch.ValidateIngest) is the precise gate; this is the cheap byte-level outer guard.
            if (req.ContentLength is long len && len > DocSearch.MaxDocBytes)
                return Results.Json(new { error = $"document too large ({len} bytes) — split it or attach a smaller file." }, statusCode: 413);

            var conv = ChatStore.Load(id);
            if (conv is null) return Results.NotFound();

            var source = (body?.Source ?? "").Trim();
            var text = body?.Text ?? "";
            if (source.Length == 0 || RecipeStore.SafeName(System.IO.Path.GetFileNameWithoutExtension(source)) is null)
                return Results.BadRequest(new { error = "bad or missing source filename" });
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "empty document text" });

            // Char-level cap: a too-long doc fails FAST with the clear validator message (never the 30s-timeout
            // "start the embed server" 503). This mirrors what IngestAsync also enforces, surfaced here as a 413.
            var tooBig = DocSearch.ValidateIngest(text);
            if (tooBig is not null) return Results.Json(new { error = tooBig }, statusCode: 413);

            // KB id == the conversation id (first slice). Ingest under it, then mark the thread attached (KbId set)
            // so RetrieveDocs fires on subsequent turns. A failed ingest (embed server down) surfaces a 503.
            var r = await DocSearch.IngestAsync(id, source, text, ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);

            if (string.IsNullOrWhiteSpace(conv.KbId))
                ChatStore.Save(conv with { KbId = id });   // graceful: a save failure doesn't undo the ingest

            return Results.Json(new { source, chunks = r.Chunks });
        });

        // ---- BINARY doc upload (.pdf/.docx): unlike the .txt/.md paste path above (the browser reads text
        // client-side and POSTs JSON), PDF/docx are BINARY — the browser uploads the raw FILE BYTES as multipart
        // and the SERVER extracts text (doc_ingest_bin -> pypdf/python-docx by extension), then reuses the EXACT
        // same chunk+embed+store pipeline. Caps: MaxDocBytes is the PRIMARY raw-bytes gate (a clean 413 BEFORE
        // buffering); SafeName guards the basename exactly as the JSON path. A missing parser dep / scanned PDF
        // degrades to a CLEAR surfaced message, never a crash — the same DocSearch contract.
        api.MapPost("/chats/{id}/docs/file", async (string id, HttpRequest req, CancellationToken ct) =>
        {
            // Raw-bytes ceiling up front (Content-Length) for a clean 413 before buffering a multi-MB upload — the
            // PRIMARY binary gate (a PDF's bytes exceed its extracted text). Mirrors the JSON path's byte guard.
            if (req.ContentLength is long clen && clen > DocSearch.MaxDocBytes)
                return Results.Json(new { error = $"document too large ({clen} bytes) — split it or attach a smaller file." }, statusCode: 413);

            var conv = ChatStore.Load(id);
            if (conv is null) return Results.NotFound();

            if (!req.HasFormContentType) return Results.BadRequest(new { error = "expected multipart/form-data" });

            // ENDPOINT-LEVEL body cap (FIX 6): the Content-Length 413 above is skipped by a CHUNKED upload (no
            // Content-Length) or a forged-low one, and the default ReadFormAsync would buffer the whole body up to
            // Kestrel's 30MB limit before the post-buffer cap fires. Cap MultipartBodyLengthLimit at ~MaxDocBytes
            // (+ framing margin) so ReadFormAsync ABORTS past it — a chunked client can't force a multi-MB buffer.
            // Honest (Content-Length-bearing) clients still get the clear 413 from the pre-check above.
            IFormCollection form;
            try
            {
                form = await req.ReadFormAsync(
                    new FormOptions { MultipartBodyLengthLimit = DocSearch.MaxUploadBytes }, ct);
            }
            catch (System.IO.InvalidDataException)   // body exceeded MultipartBodyLengthLimit -> the same 413 contract
            {
                return Results.Json(new { error = $"document too large — split it or attach a smaller file (max {DocSearch.MaxDocBytes} bytes)." }, statusCode: 413);
            }
            var file = form.Files["file"];
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "no file uploaded" });

            // Source label = the form 'source' (basename) or the uploaded filename; SafeName-guard the basename
            // exactly as the JSON path (no path/traversal surface — kb_id is the conversation id, never client-set).
            var rawSource = ((string?)form["source"] ?? file.FileName ?? "").Trim();
            var source = System.IO.Path.GetFileName(rawSource);
            if (source.Length == 0 || RecipeStore.SafeName(System.IO.Path.GetFileNameWithoutExtension(source)) is null)
                return Results.BadRequest(new { error = "bad or missing source filename" });

            // Buffer the (already byte-capped) upload, then hand the raw bytes to doc_ingest_bin. The server-side
            // extractor routes by extension and the EXISTING ValidateIngest/MAX_CHUNKS backstops bound the result.
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            if (bytes.LongLength > DocSearch.MaxDocBytes)
                return Results.Json(new { error = $"document too large ({bytes.LongLength} bytes) — split it or attach a smaller file." }, statusCode: 413);

            var r = await DocSearch.IngestBinAsync(id, source, bytes, ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);

            if (string.IsNullOrWhiteSpace(conv.KbId))
                ChatStore.Save(conv with { KbId = id });   // graceful: a save failure doesn't undo the ingest

            // chunks==0 on a successful exit means a scanned/image-only PDF (no text layer) — surface a hint so the
            // user isn't confused by a silent no-op (OCR is out of scope for v0.14, a labeled follow-up).
            var note = r.Chunks == 0
                ? "no text found — looks like a scanned/image PDF (OCR not yet supported)."
                : null;
            return Results.Json(new { source, chunks = r.Chunks, note });
        });

        api.MapGet("/chats/{id}/docs", async (string id, CancellationToken ct) =>
        {
            if (ChatStore.Load(id) is null) return Results.NotFound();
            var srcs = await DocSearch.SourcesAsync(id, ct);
            return Results.Json(srcs.Select(s => new { source = s.source, chunks = s.chunks }));
        });

        api.MapDelete("/chats/{id}/docs/{source}", async (string id, string source, CancellationToken ct) =>
        {
            var conv = ChatStore.Load(id);
            if (conv is null) return Results.NotFound();
            if (!await DocSearch.RemoveAsync(id, source, ct)) return Results.NotFound();

            // If that was the LAST attached source, clear KbId so RetrieveDocs short-circuits (it otherwise keeps
            // spawning a doc_search every turn over an empty KB). Best-effort: a sources-list/save hiccup never
            // fails the remove the user already got. (The KB id IS the thread id in the first slice.)
            if (!string.IsNullOrWhiteSpace(conv.KbId))
            {
                try
                {
                    var remaining = await DocSearch.SourcesAsync(id, ct);
                    if (remaining.Count == 0) ChatStore.Save(conv with { KbId = null });
                }
                catch { /* leave KbId as-is on a cleanup hiccup; retrieval still degrades gracefully */ }
            }
            return Results.Ok();
        });

        // ---- pending image-gen queued FROM CHAT (the generate_image tool's durable side; the create view surfaces
        // it so the user can pick it up after flipping the GPU to MEDIA). Id is server-generated => no client path. ----
        api.MapGet("/pending-gen", () => Results.Json(PendingGenStore.List()));
        api.MapDelete("/pending-gen/{id}", (string id) => PendingGenStore.Delete(id) ? Results.Ok() : Results.NotFound());

        // ---- multi-character composer (base scene + isolated per-character regions -> one raw SwarmUI prompt) ----
        // Pure compile (no GPU); the SPA generates the result via /api/generate with raw=true so the <object:..>
        // regional tags reach SwarmUI unrewritten.
        api.MapPost("/compose/multichar", async (MultiCharSpec body, CancellationToken ct) =>
        {
            var spec = body;
            var model = LlmTiers.Resolve(body.Tier);
            var rel = (body.Relationship ?? "").Trim();
            // Optional: route the relationship through the LLM at the chosen tier into a vivid interaction phrase.
            // No tier => literal text (the pure default); LLM down => falls back to literal too.
            if (model is not null && rel.Length > 0)
            {
                const string sys = "Rewrite the user's character interaction into ONE vivid, concrete scene-action phrase "
                    + "for an image prompt (who does what to whom — posture, contact, expression). Output ONLY the phrase "
                    + "— no preamble, no quotes, keep the names.";
                var chat = await LocalLlm.ChatAsync(sys, rel, 0.7, 120, ct, model).ConfigureAwait(false);
                if (chat.Ok && !string.IsNullOrWhiteSpace(chat.Text)) spec = body with { Relationship = Rewriter.CleanRewrite(chat.Text) };
            }
            return Results.Json(new { prompt = MultiCharacter.Compile(spec) });
        });

        // ---- camera compiler (structured cinematography -> a prompt phrase for video/i2v; pure, no GPU) ----
        api.MapPost("/compose/camera", (CameraSpec body) => Results.Json(new { phrase = Camera.Phrase(body) }));

        // ---- in-app LoRA training (kohya sd-scripts sidecar, gated on setup.ps1 -Train; output -> LoRA mixer) ----
        api.MapPost("/train", async (TrainRequest body, GalleryService gal, CancellationToken ct) =>
        {
            var r = await Training.TrainAsync(body, gal, ct);
            return r.Ok ? Results.Json(new { ok = true, message = r.Message }) : Results.Json(new { error = r.Message }, statusCode: 503);
        });

        // ---- SAM point segmentation (semantic click->mask; sidecar, gated on setup.ps1 -Sam) ----
        api.MapPost("/segment-click", async (SegmentClickRequest body, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(body.Image) || !body.Image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "image (data URL) required" });
            string imgPath;
            try
            {
                var comma = body.Image.IndexOf(',');
                imgPath = Path.Combine(Path.GetTempPath(), $"dokidex-sam-{Guid.NewGuid():N}.png");
                File.WriteAllBytes(imgPath, Convert.FromBase64String(body.Image[(comma + 1)..]));
            }
            catch { return Results.BadRequest(new { error = "bad image" }); }
            var r = await Sam.SegmentAsync(imgPath, body.X, body.Y, ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);
            var b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(r.MaskPath!, ct));
            return Results.Json(new { mask = $"data:image/png;base64,{b64}" });
        });

        // ---- 3D blockout: a software depth rasterizer (primitives -> perspective+occluded depth map), no GPU ----
        api.MapPost("/blockout", (BlockoutScene body) =>
        {
            var depth = Blockout.RenderDepth(body);
            return Results.Json(new { width = Math.Clamp(body.Width, 1, 1024), height = Math.Clamp(body.Height, 1, 1024), depth = Convert.ToBase64String(depth) });
        });

        // ---- exploration mode: diverge one prompt into N seed-varied gens (reuses the gen queue) ----
        api.MapPost("/explore", (ExploreRequest body, GenerationJobs jobs) =>
        {
            if (string.IsNullOrWhiteSpace(body.Prompt)) return Results.BadRequest(new { error = "empty prompt" });
            var kind = (body.Kind ?? "image").Trim().ToLowerInvariant();
            if (Array.IndexOf(GenRequest.Kinds, kind) < 0) kind = "image";
            var ids = new List<string>();
            foreach (var seed in Explore.Seeds(body.Seed, body.Count))
            {
                var req = new GenRequest(body.Prompt.Trim(), kind, Fast: body.Fast, Seed: seed,
                    Aspect: body.Aspect, Lora: body.Lora, Negative: body.Negative);
                ids.Add(jobs.Submit(req).Id);
            }
            return Results.Json(new { submitted = ids.Count, ids });
        });

        // ---- style chips (stackable aesthetic bundles -> appended +/- prompt fragments; pure, no GPU) ----
        api.MapGet("/style-chips", () => Results.Json(StyleChips.All()));
        api.MapPost("/compose/style", (StyleRequest body) =>
        {
            var (p, n) = StyleChips.Compose(body.Prompt, body.Negative, body.Chips);
            return Results.Json(new { prompt = p, negative = n });
        });

        // ---- steerable rewriter (user-directed prompt rewrite via the local LLM; conversational iterate) ----
        api.MapPost("/rewrite", async (RewriteRequest body, CancellationToken ct) =>
        {
            var r = await Rewriter.RewriteAsync(body.Prompt ?? "", body.Instruction ?? "", ct);
            return r.Ok ? Results.Json(new { prompt = r.Prompt }) : Results.Json(new { error = r.Error }, statusCode: 503);
        });

        // ---- vision (gated on a vision-capable local model): reverse-prompt a gallery image, QA-verify a card ----
        api.MapPost("/describe", async (DescribeRequest body, GalleryService gal, CancellationToken ct) =>
        {
            var url = gal.ImageDataUrl(body.Name ?? "");
            if (url is null) return Results.Json(new { error = "unknown image" }, statusCode: 404);
            var r = await Vision.DescribeAsync(url, ct);
            return r.Ok ? Results.Json(new { prompt = r.Text }) : Results.Json(new { error = r.Error }, statusCode: 503);
        });
        api.MapPost("/verify", async (VerifyRequest body, GalleryService gal, CancellationToken ct) =>
        {
            var url = gal.ImageDataUrl(body.Name ?? "");
            if (url is null) return Results.Json(new { error = "unknown image" }, statusCode: 404);
            var (ok, verdict, err) = await Vision.VerifyAsync(url, body.Prompt ?? "", ct);
            return ok ? Results.Json(new { pass = verdict!.Pass, reason = verdict.Reason }) : Results.Json(new { error = err }, statusCode: 503);
        });

        // ---- gated ffmpeg video tools: extract a keyframe, join clips (clip-extend / storyboard primitives) ----
        api.MapPost("/extract-frame", async (FrameRequest body, GalleryService gal, DokiService doki, CancellationToken ct) =>
        {
            var src = gal.Resolve(body.Name ?? "");
            if (src is null) return Results.NotFound();
            var outPath = doki.NewGenOutPath("image");
            var r = await Ffmpeg.RunAsync(Ffmpeg.ExtractFrameArgs(src, outPath, body.Last), ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);
            GalleryService.WriteSidecar(outPath, "frame", "image", $"{(body.Last ? "last" : "first")} frame of {Path.GetFileName(src)}", Path.GetFileName(src));
            var name = Path.GetFileName(outPath);
            return Results.Json(new { name, mediaUrl = $"/api/gallery/media/{Uri.EscapeDataString(name)}" });
        });
        api.MapPost("/join-clips", async (JoinRequest body, GalleryService gal, DokiService doki, CancellationToken ct) =>
        {
            var inputs = (body.Names ?? new()).Select(n => gal.Resolve(n)).Where(p => p is not null).Select(p => p!).ToList();
            if (inputs.Count < 2) return Results.BadRequest(new { error = "select at least two clips to join" });
            var outPath = doki.NewGenOutPath("video");
            var r = await Ffmpeg.RunAsync(Ffmpeg.ConcatArgs(inputs, outPath), ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);
            GalleryService.WriteSidecar(outPath, "join", "video", $"joined {inputs.Count} clips");
            var name = Path.GetFileName(outPath);
            return Results.Json(new { name, mediaUrl = $"/api/gallery/media/{Uri.EscapeDataString(name)}" });
        });

        // ---- Parallel multi-model compare: one prompt -> one fast job per installed image base (grid) ----
        api.MapPost("/compare", (CompareRequest body, GenerationJobs jobs, ModelManager mm) =>
        {
            if (string.IsNullOrWhiteSpace(body.Prompt)) return Results.BadRequest(new { error = "empty prompt" });
            var models = mm.InstalledImageModels();
            if (models.Count == 0) return Results.BadRequest(new { error = "no installed image bases to compare" });
            var ids = models.Select(m => jobs.Submit(new GenRequest(body.Prompt!.Trim(), "image", Fast: true, Model: m.File)).ToDto()).ToList();
            return Results.Json(new { submitted = ids.Count, jobs = ids });
        });

        // ---- Image Set / series: shared style+aspect locked, per-cell prompt+seed -> one job per cell ----
        api.MapPost("/series", (SeriesSpec body, GenerationJobs jobs) =>
        {
            var reqs = ImageSet.Compile(body);
            if (reqs.Count == 0) return Results.BadRequest(new { error = "no cells with a prompt" });
            var ids = reqs.Select(r => jobs.Submit(r).ToDto()).ToList();
            return Results.Json(new { submitted = ids.Count, jobs = ids });
        });

        // ---- CSV batch generation: header row -> per-row GenRequest -> queued jobs (respects the GPU gate) ----
        api.MapPost("/batch", (BatchRequest body, GenerationJobs jobs) =>
        {
            var ids = RunBatchCsv(body.Csv, jobs);
            return Results.Json(new { submitted = ids.Count, ids });
        });

        // ---- @-reference shelf: named reusable prompt snippets (@name expands via the recipe) ----
        api.MapGet("/references", () => Results.Json(References.List()));
        api.MapPost("/references", (ReferenceDto body) =>
            References.Save(body.Name, body.Text) ? Results.Ok() : Results.BadRequest(new { error = "bad reference name" }));
        api.MapDelete("/references/{name}", (string name) => References.Delete(name) ? Results.Ok() : Results.NotFound());

        // ---- saved recipes: named, reusable pipelines (CSV of steps) — the linear/persistent slice of node-flow ----
        api.MapGet("/recipes", () => Results.Json(RecipeStore.List()));
        api.MapGet("/recipes/{name}", (string name) =>
            RecipeStore.Load(name) is { } csv ? Results.Json(new { name, csv }) : Results.NotFound());
        api.MapPost("/recipes", (RecipeDto body) =>
            RecipeStore.Save(body.Name, body.Csv) ? Results.Ok() : Results.BadRequest(new { error = "bad recipe name" }));
        api.MapDelete("/recipes/{name}", (string name) => RecipeStore.Delete(name) ? Results.Ok() : Results.NotFound());
        api.MapPost("/recipes/{name}/run", (string name, GenerationJobs jobs) =>
        {
            var csv = RecipeStore.Load(name);
            if (csv is null) return Results.NotFound();
            var ids = RunBatchCsv(csv, jobs);
            return Results.Json(new { submitted = ids.Count, ids });
        });

        // ---- Demucs stem separation (standalone sidecar; splits a gallery audio item into stems) ----
        api.MapPost("/stems", async (StemRequest body, GalleryService gal, CancellationToken ct) =>
        {
            var src = gal.Resolve(body.Name ?? "");   // scoped to the gallery folder (no arbitrary paths)
            if (src is null) return Results.NotFound();
            var r = await Demucs.SeparateAsync(src, body.Model, ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);
            var track = Path.GetFileNameWithoutExtension(src);
            var urls = new List<string>();
            foreach (var stem in r.Stems)   // copy each stem into the gallery root so it serves + appears in the Library
            {
                var name = $"{track}-{Path.GetFileNameWithoutExtension(stem)}.wav";
                var dest = Path.Combine(DokiService.GenDir, name);
                try { File.Copy(stem, dest, true); GalleryService.WriteSidecar(dest, "stem", "stem", $"{track} · {Path.GetFileNameWithoutExtension(stem)}"); urls.Add($"/api/gallery/media/{Uri.EscapeDataString(name)}"); } catch { }
            }
            return Results.Json(new { stems = urls });
        });

        // ---- node-lite flow: a DAG of gen steps -> topological order -> queued in dependency order ----
        api.MapPost("/graph/run", (GraphSpec body, GenerationJobs jobs) =>
        {
            var nodes = body.Nodes ?? new();
            var order = GraphRunner.ExecutionOrder(nodes, body.Edges ?? new());
            if (order is null) return Results.BadRequest(new { error = "the flow has a cycle" });
            var byId = nodes.Where(n => !string.IsNullOrWhiteSpace(n.Prompt)).ToDictionary(n => n.Id);
            var ids = new List<string>();
            foreach (var id in order)
            {
                if (!byId.TryGetValue(id, out var n)) continue;
                var kind = (n.Kind ?? "image").Trim().ToLowerInvariant();
                if (Array.IndexOf(GenRequest.Kinds, kind) < 0) kind = "image";
                ids.Add(jobs.Submit(new GenRequest(n.Prompt!.Trim(), kind, Fast: n.Fast, Seed: n.Seed,
                    Aspect: n.Aspect, Lora: n.Lora, Negative: n.Negative)).Id);
            }
            return Results.Json(new { submitted = ids.Count, ids });
        });

        // ---- text-to-speech (Chatterbox :8004); voices = file-based registry, output lands in the Library ----
        api.MapGet("/voices", () => Results.Json(Tts.Voices()));
        api.MapPost("/speak", async (SpeakRequest body, CancellationToken ct) =>
        {
            var r = await Tts.SpeakAsync(body, ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);
            var name = Path.GetFileName(r.ArtifactPath!);
            return Results.Json(new { mediaUrl = $"/api/gallery/media/{Uri.EscapeDataString(name)}" });
        });
        // multi-speaker dialogue: a "HERO: hi" script -> per-line synth routed to each speaker's voice,
        // concatenated into one Library clip. Speaker->voice map + optional pronunciation lexicon.
        api.MapPost("/dialogue", async (DialogueRequest body, CancellationToken ct) =>
        {
            var lines = Dialogue.Parse(body.Script);
            if (lines.Count == 0) return Results.Json(new { error = "no dialogue lines — use \"NAME: line\"" }, statusCode: 400);
            var cast = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in body.Cast ?? new()) if (!string.IsNullOrWhiteSpace(c.Speaker)) cast[c.Speaker!.Trim()] = (c.Voice ?? "").Trim();
            var r = await Tts.SpeakDialogueAsync(lines, cast, body.Lexicon, ct);
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: 503);
            var name = Path.GetFileName(r.ArtifactPath!);
            return Results.Json(new { mediaUrl = $"/api/gallery/media/{Uri.EscapeDataString(name)}", lines = lines.Count });
        });
    }

    // Parse a CSV (header row + per-row params) and submit one gen per row through the queue; returns job ids.
    // Shared by /api/batch (one-shot) and /api/recipes/{name}/run (saved pipeline).
    private static List<string> RunBatchCsv(string? csv, GenerationJobs jobs)
    {
        var ids = new List<string>();
        foreach (var row in Csv.ParseWithHeader(csv))
        {
            row.TryGetValue("prompt", out var prompt);
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            var kind = (Cell(row, "kind") ?? "image").Trim().ToLowerInvariant();
            if (Array.IndexOf(GenRequest.Kinds, kind) < 0) kind = "image";
            var req = new GenRequest(prompt.Trim(), kind,
                Fast: BoolCell(row, "fast"), Upscale: BoolCell(row, "upscale"), Refine: BoolCell(row, "refine"),
                Face: BoolCell(row, "face"), Realism: BoolCell(row, "realism"), Raw: BoolCell(row, "raw"),
                Seed: IntCell(row, "seed", -1), Count: Math.Clamp(IntCell(row, "count", 1), 1, 9),
                Strength: DblCell(row, "strength", -1), Aspect: Cell(row, "aspect"), Lora: Cell(row, "lora"),
                Negative: Cell(row, "negative"));
            ids.Add(jobs.Submit(req).Id);
        }
        return ids;
    }

    // CSV cell helpers for the batch endpoint (tolerant: missing/blank -> the default).
    private static string? Cell(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    private static bool BoolCell(Dictionary<string, string> row, string key)
    {
        var v = Cell(row, key)?.Trim().ToLowerInvariant();
        return v is "1" or "true" or "yes" or "y" or "on";
    }
    private static int IntCell(Dictionary<string, string> row, string key, int dflt)
        => int.TryParse(Cell(row, key), out var n) ? n : dflt;
    private static double DblCell(Dictionary<string, string> row, string key, double dflt)
        => double.TryParse(Cell(row, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : dflt;

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
