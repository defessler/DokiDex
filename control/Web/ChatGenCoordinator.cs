using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// The GPU-flip render coordinator for chat-queued image gens. The chat turn runs with the coder LLM resident
// on the single 32 GB GPU; SwarmUI (the renderer) is GPU-exclusive with it (26 GB LLM + 18 GB SwarmUI > 32 GB —
// decisions.md), so generate_image / edit_image only QUEUE a PendingGen mid-chat — nothing renders while the LLM
// is loaded. This coordinator is the OTHER half of that round-trip: when invoked (a user "render queued" action,
// or the host triggering a drain), it drains PendingGenStore.Queued() and renders each pending gen over the GPU,
// one at a time, flipping the GPU to media mode first and (optionally) back to the agent LLM after.
//
// The whole point is to keep the GPU/SwarmUI/disk OUT of the unit tests: every side-effecting step is behind an
// injected delegate (a Deps bundle), and every DECISION is a pure static (NeedsFlip / BuildGenRequest /
// GalleryRelative). A test constructs a coordinator with fakes and drives DrainAsync to assert the exact sequence
// — flip-to-media (only when needed), render oldest-first, mark done + write the sidecar, flip back — with NO real
// GPU, SwarmUI, or filesystem touched. Production wires the Deps to the real DokiService / SwarmGen /
// PendingGenStore / GalleryService.
//
// Single-flight: DrainAsync serializes the whole drain behind a SemaphoreSlim, so a second trigger while a drain
// is in flight no-ops rather than double-rendering the queue over the mutually-exclusive GPU.
public sealed class ChatGenCoordinator
{
    // ---- pure decisions (the unit-test seams; no GPU/SwarmUI/disk) ----

    // The renderer must own the GPU before a gen can run. The LLM and SwarmUI are mutually exclusive on the one
    // card, so any active group that ISN'T "media" (the LLM group, or "none") needs a flip to media first.
    // Case-insensitive; a null/blank group is treated as not-media (flip). Total + side-effect-free.
    public static bool NeedsFlip(string? activeGroup)
        => !string.Equals((activeGroup ?? "").Trim(), "media", StringComparison.OrdinalIgnoreCase);

    // PURE: translate a durable PendingGen into the GenRequest the render pipeline consumes, carrying exactly the
    // fields the chat queue persisted — Prompt / Kind / Model / Count, and (for edit_image / img2img) the InitImage
    // + Strength. A blank/missing kind falls back to "image" (the chat queue is image-family only). Strength is
    // applied only when an init image is present AND a real (>= 0) strength was queued, mirroring GenRequest's
    // "-1 = recipe default" convention so an un-set edit still uses the recipe creativity. Total + side-effect-free
    // => unit-tested, so the carry-through can't silently drop a field.
    public static GenRequest BuildGenRequest(PendingGen p)
    {
        var kind = string.IsNullOrWhiteSpace(p.Kind) ? "image" : p.Kind.Trim();
        var init = string.IsNullOrWhiteSpace(p.InitImage) ? null : p.InitImage;
        // -1 = recipe default (GenRequest's convention); only carry a real queued strength, and only with an init.
        var strength = (init is not null && p.Strength is double s && s >= 0) ? s : -1d;
        return new GenRequest(
            Prompt: p.Prompt ?? "",
            Kind: kind,
            Model: string.IsNullOrWhiteSpace(p.Model) ? null : p.Model,
            Count: p.Count < 1 ? 1 : p.Count,
            InitImage: init,
            Strength: strength);
    }

    // PURE: the gallery-relative result path persisted onto the PendingGen (ResultRel) on success. The library is a
    // flat folder keyed by file name (GalleryService.Resolve takes a bare name, no subdirs/traversal), so the
    // relative handle is simply the artifact's file name. Null/blank in => "" (the caller still marks done).
    public static string GalleryRelative(string? artifactPath)
        => string.IsNullOrWhiteSpace(artifactPath) ? "" : Path.GetFileName(artifactPath);

    // ---- injected side-effecting seams (real in prod, fakes in tests) ----

    // The behaviors the coordinator needs that touch the GPU / SwarmUI / disk, each behind a delegate so a unit
    // test can drive the whole drain with fakes. Production builds this from DokiService / SwarmGen /
    // PendingGenStore / GalleryService (see the public ctor below).
    public sealed class Deps
    {
        // The current GPU group ("llm" | "media" | "none"), via DokiService.GetStatusAsync().Gpu.ActiveGroup.
        public required Func<CancellationToken, Task<string>> GetActiveGroup { get; init; }
        // Flip the GPU to a profile ("media" to evict the LLM and load SwarmUI; "agent" to flip back).
        public required Action<string> SwitchMode { get; init; }
        // Wait until SwarmUI answers GetNewSession at 127.0.0.1:7801 after a flip (bounded retry). True = ready,
        // false = gave up (the render of this item is then skipped/failed with a clear message).
        public required Func<CancellationToken, Task<bool>> WaitForSwarmReady { get; init; }
        // The drain work queue: the queued pending-gens, oldest-first (PendingGenStore.Queued()).
        public required Func<IReadOnlyList<PendingGen>> Queued { get; init; }
        // Render ONE gen: build the SwarmUI body + drive GenerateText2ImageWS, reporting progress; returns the
        // artifact outcome. In prod this is the SwarmGen.RunAsync bridge (see BuildRenderFn).
        public required Func<GenRequest, Action<SwarmGen.Progress>, CancellationToken, Task<SwarmGen.Outcome>> Render { get; init; }
        // Persist a lifecycle transition (queued->rendering->done/failed) + optional result/error/preview.
        public required Action<string, string, string?, string?, string?> SetStatus { get; init; }
        // Write the Library sidecar for a finished artifact (so the gen appears in the gallery).
        public required Action<string, string, string, string> WriteSidecar { get; init; }
        // Optionally flip the GPU back to the agent LLM after the queue drains (so chat resumes immediately).
        // Set false to leave the GPU in media mode (e.g. the user is staying in the Media composer).
        public bool FlipBackToAgentWhenDone { get; init; } = true;
    }

    private readonly Deps _deps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Test/explicit ctor: inject every side-effecting seam. The core sequencing is exercised here with fakes.
    public ChatGenCoordinator(Deps deps) => _deps = deps;

    // Production ctor: wire the Deps to the real control plane. SwitchMode goes through DokiService.Up (which
    // evicts the opposite GPU group); readiness is polled against SwarmUI's GetNewSession; render is the SwarmGen
    // bridge; the store/sidecar writes go to PendingGenStore/GalleryService. `flipBackToAgentWhenDone` controls
    // whether the GPU returns to the LLM after the drain (default true so chat resumes).
    public ChatGenCoordinator(DokiService doki, bool flipBackToAgentWhenDone = true)
        : this(BuildLiveDeps(doki, flipBackToAgentWhenDone)) { }

    private static Deps BuildLiveDeps(DokiService doki, bool flipBack) => new()
    {
        GetActiveGroup = async ct =>
        {
            var st = await doki.GetStatusAsync(ct).ConfigureAwait(false);
            return st?.Gpu?.ActiveGroup ?? "none";
        },
        SwitchMode = profile => doki.Up(profile),
        WaitForSwarmReady = SwarmReadyAsync,
        Queued = () => PendingGenStore.Queued(),
        Render = (req, onProgress, ct) => RenderLiveAsync(doki, req, onProgress, ct),
        SetStatus = (id, status, resultRel, error, preview) => PendingGenStore.SetStatus(id, status, resultRel, error, preview),
        WriteSidecar = (artifactPath, id, kind, prompt) => GalleryService.WriteSidecar(artifactPath, id, kind, prompt),
        FlipBackToAgentWhenDone = flipBack,
    };

    // Live render: own the app's output path (DokiService.NewGenOutPath) + build the BodyOnly body, then drive
    // SwarmGen over the WebSocket. A null/empty body => a clean failure Outcome (never a throw). The artifact path
    // in the Outcome is the app-owned file SwarmGen downloaded to.
    private static async Task<SwarmGen.Outcome> RenderLiveAsync(DokiService doki, GenRequest req, Action<SwarmGen.Progress> onProgress, CancellationToken ct)
    {
        var body = await doki.GetGenBodyAsync(req, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return new SwarmGen.Outcome(false, null, "could not build the generation request");
        var outPath = doki.NewGenOutPath(req.Kind);
        return await SwarmGen.RunAsync(body!, outPath, onProgress, ct).ConfigureAwait(false);
    }

    // Poll SwarmUI's GetNewSession until it answers (media mode finished loading) or we give up. Bounded: ~60
    // attempts × 1s ≈ a minute, plenty for SwarmUI to come up after a GPU flip, but never an unbounded hang.
    // Reuses SwarmGen's reachability shape: a successful gen body run against GetNewSession means it's live.
    private static async Task<bool> SwarmReadyAsync(CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var c = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using var r = await http.PostAsync("http://127.0.0.1:7801/API/GetNewSession", c, ct).ConfigureAwait(false);
                if (r.IsSuccessStatusCode) return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* not up yet */ }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
        }
        return false;
    }

    // Drain ALL currently-queued chat gens, rendering each over the single GPU, one at a time. Single-flight: a
    // concurrent call while a drain is in flight returns immediately (TryWait fails) rather than double-rendering.
    // Sequence:
    //   1. snapshot the queued items (oldest-first) — empty queue => nothing to do, no GPU flip.
    //   2. if the GPU isn't already on media, flip to media and wait for SwarmUI to answer (bounded).
    //      readiness failure => fail EVERY queued item with a clear message (no half-rendered queue).
    //   3. render each item: rendering -> (Ok) done + sidecar + ResultRel, or (fail) failed + error.
    //   4. after the queue drains, optionally flip the GPU back to the agent LLM (so chat resumes).
    // The render is wrapped per-item so one bad gen can't abort the rest of the queue.
    public async Task DrainAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;   // single-flight: a drain is already running
        try
        {
            var items = _deps.Queued();
            if (items is null || items.Count == 0) return;

            // 1 flip-to-media (only when the GPU isn't already there), then wait for SwarmUI.
            var active = await _deps.GetActiveGroup(ct).ConfigureAwait(false);
            if (NeedsFlip(active))
            {
                _deps.SwitchMode("media");
                var ready = await _deps.WaitForSwarmReady(ct).ConfigureAwait(false);
                if (!ready)
                {
                    foreach (var p in items)
                        _deps.SetStatus(p.Id, "failed", null, "SwarmUI did not come up after switching to Media mode — try again.", null);
                    return;
                }
            }

            // 2 render each queued item over the now-media GPU, oldest-first, one at a time.
            foreach (var p in items)
            {
                ct.ThrowIfCancellationRequested();
                await RenderOneAsync(p, ct).ConfigureAwait(false);
            }

            // 3 the queue is drained — optionally flip back to the agent LLM so chat resumes immediately.
            if (_deps.FlipBackToAgentWhenDone) _deps.SwitchMode("agent");
        }
        finally { _gate.Release(); }
    }

    // Render a single pending gen, mapping its lifecycle onto the store: rendering (with streamed previews) ->
    // done (+ gallery sidecar + ResultRel) or failed (+ error). Per-item try/catch so a single render failure marks
    // just that item failed and the drain continues with the rest of the queue.
    private async Task RenderOneAsync(PendingGen p, CancellationToken ct)
    {
        try
        {
            _deps.SetStatus(p.Id, "rendering", null, null, null);
            var req = BuildGenRequest(p);
            var outcome = await _deps.Render(req,
                prog => { if (!string.IsNullOrEmpty(prog.PreviewDataUrl)) _deps.SetStatus(p.Id, "rendering", null, null, prog.PreviewDataUrl); },
                ct).ConfigureAwait(false);

            if (outcome.Ok && !string.IsNullOrWhiteSpace(outcome.ArtifactPath))
            {
                _deps.WriteSidecar(outcome.ArtifactPath!, p.Id, req.Kind, p.Prompt ?? "");
                _deps.SetStatus(p.Id, "done", GalleryRelative(outcome.ArtifactPath), null, null);
            }
            else
            {
                _deps.SetStatus(p.Id, "failed", null, string.IsNullOrWhiteSpace(outcome.Message) ? "generation failed" : outcome.Message, null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _deps.SetStatus(p.Id, "failed", null, "cancelled", null);
            throw;
        }
        catch (Exception ex)
        {
            _deps.SetStatus(p.Id, "failed", null, ex.Message, null);
        }
    }
}
