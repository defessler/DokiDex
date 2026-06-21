using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Control.Services;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The GPU-flip render coordinator (the chat->media round-trip's render half). A chat turn runs with the LLM
// resident on the single 32GB GPU; SwarmUI is GPU-exclusive with it, so generate_image only QUEUES a PendingGen.
// This coordinator drains that queue, flipping the GPU to media mode, rendering each gen oldest-first over the one
// card, marking the lifecycle (rendering->done/failed), then flipping back to the agent LLM.
//
// The CORE sequencing is kept pure + injectable so these tests exercise the WHOLE drain with FAKES — no real GPU,
// SwarmUI, or disk. Every side-effecting step is a delegate on ChatGenCoordinator.Deps; every decision is a pure
// static (NeedsFlip / BuildGenRequest / GalleryRelative). The tests assert the exact sequence (flip-to-media only
// when needed, render FIFO, mark done + sidecar, flip back) and the lifecycle transitions, GPU-free.
public class ChatGenCoordinatorTests
{
    // ---- pure decisions ----

    [Theory]
    [InlineData("llm", true)]      // LLM resident -> must flip to media
    [InlineData("none", true)]     // nothing loaded -> must flip to media
    [InlineData("", true)]         // unknown/blank -> treat as not-media -> flip
    [InlineData(null, true)]
    [InlineData("media", false)]   // already on media -> no flip
    [InlineData("MEDIA", false)]   // case-insensitive
    [InlineData(" media ", false)] // trimmed
    public void NeedsFlip_is_true_unless_the_gpu_is_already_on_media(string? activeGroup, bool expected)
        => Assert.Equal(expected, ChatGenCoordinator.NeedsFlip(activeGroup));

    [Fact]
    public void BuildGenRequest_carries_prompt_kind_model_and_count()
    {
        var p = Pending("a neon dragon at night", kind: "image", model: "auto", count: 4);
        var req = ChatGenCoordinator.BuildGenRequest(p);

        Assert.Equal("a neon dragon at night", req.Prompt);
        Assert.Equal("image", req.Kind);
        Assert.Equal("auto", req.Model);
        Assert.Equal(4, req.Count);
        Assert.Null(req.InitImage);
        Assert.Equal(-1, req.Strength);   // no init image => recipe-default creativity
    }

    [Fact]
    public void BuildGenRequest_maps_an_edit_to_img2img_with_init_image_and_strength()
    {
        var p = Pending("refine the hair", kind: "edit", initImage: "src.png", strength: 0.55);
        var req = ChatGenCoordinator.BuildGenRequest(p);

        Assert.Equal("edit", req.Kind);
        Assert.Equal("src.png", req.InitImage);     // img2img source carried
        Assert.Equal(0.55, req.Strength);           // queued creativity carried
    }

    [Fact]
    public void BuildGenRequest_defaults_a_blank_kind_to_image_and_null_model()
    {
        var p = Pending("p", kind: "  ", model: "   ", count: 0);
        var req = ChatGenCoordinator.BuildGenRequest(p);

        Assert.Equal("image", req.Kind);   // blank kind -> image family
        Assert.Null(req.Model);            // blank model -> recipe default
        Assert.Equal(1, req.Count);        // count clamped up to >= 1
    }

    [Fact]
    public void BuildGenRequest_ignores_a_strength_with_no_init_image()
    {
        // Strength only means something for img2img/edit; without an init image it stays the recipe default (-1)
        // so a stray queued strength can't accidentally turn a fresh text2img into a weak img2img.
        var p = Pending("p", strength: 0.9);
        var req = ChatGenCoordinator.BuildGenRequest(p);
        Assert.Null(req.InitImage);
        Assert.Equal(-1, req.Strength);
    }

    [Fact]
    public void GalleryRelative_is_the_artifact_file_name()
    {
        Assert.Equal("image-20260621-120000-7.png",
            ChatGenCoordinator.GalleryRelative(@"C:\Users\me\DokiGen\image-20260621-120000-7.png"));
        Assert.Equal("", ChatGenCoordinator.GalleryRelative(null));
        Assert.Equal("", ChatGenCoordinator.GalleryRelative("   "));
    }

    // ---- the full drain sequence, GPU-free, via injected fakes ----

    [Fact]
    public async Task DrainAsync_flips_to_media_renders_oldest_first_marks_done_then_flips_back()
    {
        var queued = new[]
        {
            Pending("first",  id: "a", created: "2026-06-21T10:00:00Z"),
            Pending("second", id: "b", created: "2026-06-21T10:01:00Z"),
        };
        var fake = new FakeDeps(active: "llm", queued: queued);
        var coord = new ChatGenCoordinator(fake.Build());

        await coord.DrainAsync();

        // flipped to media BEFORE any render, waited for SwarmUI, then flipped back to the agent at the end.
        Assert.Equal(new[] { "media", "agent" }, fake.ModeSwitches);
        Assert.Equal(1, fake.ReadyWaits);

        // rendered oldest-first (a before b).
        Assert.Equal(new[] { "first", "second" }, fake.RenderedPrompts);

        // each item walked rendering -> done with a gallery-relative result + a sidecar.
        Assert.Equal("done", fake.StatusOf("a"));
        Assert.Equal("done", fake.StatusOf("b"));
        Assert.Equal(new[] { "rendering", "done" }, fake.TransitionsOf("a"));
        Assert.Equal(new[] { "rendering", "done" }, fake.TransitionsOf("b"));
        Assert.Equal(2, fake.Sidecars.Count);
        Assert.All(fake.DoneResultRels, r => Assert.False(string.IsNullOrWhiteSpace(r)));
    }

    [Fact]
    public async Task DrainAsync_does_not_flip_when_already_on_media()
    {
        var fake = new FakeDeps(active: "media", queued: new[] { Pending("p", id: "a") });
        var coord = new ChatGenCoordinator(fake.Build());

        await coord.DrainAsync();

        // already on media: no flip-to-media, no readiness wait. Only the flip-BACK at the end remains.
        Assert.DoesNotContain("media", fake.ModeSwitches);
        Assert.Equal(0, fake.ReadyWaits);
        Assert.Equal(new[] { "agent" }, fake.ModeSwitches);
        Assert.Equal("done", fake.StatusOf("a"));
    }

    [Fact]
    public async Task DrainAsync_with_an_empty_queue_does_nothing_no_gpu_flip()
    {
        var fake = new FakeDeps(active: "llm", queued: Array.Empty<PendingGen>());
        var coord = new ChatGenCoordinator(fake.Build());

        await coord.DrainAsync();

        Assert.Empty(fake.ModeSwitches);     // never touch the GPU for an empty queue
        Assert.Equal(0, fake.ReadyWaits);
        Assert.Empty(fake.RenderedPrompts);
    }

    [Fact]
    public async Task DrainAsync_can_be_told_to_leave_the_gpu_on_media()
    {
        var fake = new FakeDeps(active: "llm", queued: new[] { Pending("p", id: "a") }) { FlipBack = false };
        var coord = new ChatGenCoordinator(fake.Build());

        await coord.DrainAsync();

        // flipped to media to render, but did NOT flip back (caller is staying in the Media composer).
        Assert.Equal(new[] { "media" }, fake.ModeSwitches);
        Assert.Equal("done", fake.StatusOf("a"));
    }

    [Fact]
    public async Task DrainAsync_marks_an_item_failed_when_the_render_fails_and_continues_the_queue()
    {
        var fake = new FakeDeps(active: "media", queued: new[]
        {
            Pending("bad",  id: "a", created: "2026-06-21T10:00:00Z"),
            Pending("good", id: "b", created: "2026-06-21T10:01:00Z"),
        });
        // "bad" returns an !Ok outcome; "good" renders fine.
        fake.RenderResult = req => req.Prompt == "bad"
            ? new SwarmGen.Outcome(false, null, "start media mode first")
            : new SwarmGen.Outcome(true, $@"C:\g\{req.Prompt}.png", "done");
        var coord = new ChatGenCoordinator(fake.Build());

        await coord.DrainAsync();

        // the failed item is marked failed with the message; the queue still rendered the next item.
        Assert.Equal("failed", fake.StatusOf("a"));
        Assert.Equal("start media mode first", fake.ErrorOf("a"));
        Assert.Equal("done", fake.StatusOf("b"));
        Assert.Equal(new[] { "bad", "good" }, fake.RenderedPrompts);   // a bad render didn't abort the drain
        Assert.Single(fake.Sidecars);   // sidecar only for the one that succeeded
    }

    [Fact]
    public async Task DrainAsync_fails_every_item_when_swarmui_never_comes_up()
    {
        var fake = new FakeDeps(active: "llm", queued: new[] { Pending("p1", id: "a"), Pending("p2", id: "b") })
        { SwarmReady = false };
        var coord = new ChatGenCoordinator(fake.Build());

        await coord.DrainAsync();

        // flipped to media, waited, gave up -> NO render, every queued item failed with a clear message.
        Assert.Equal(new[] { "media" }, fake.ModeSwitches);   // flipped to media but never flipped back (nothing rendered)
        Assert.Equal(1, fake.ReadyWaits);
        Assert.Empty(fake.RenderedPrompts);
        Assert.Equal("failed", fake.StatusOf("a"));
        Assert.Equal("failed", fake.StatusOf("b"));
        Assert.Contains("SwarmUI did not come up", fake.ErrorOf("a"));
    }

    [Fact]
    public async Task DrainAsync_streams_in_flight_previews_as_rendering_updates()
    {
        var fake = new FakeDeps(active: "media", queued: new[] { Pending("p", id: "a") });
        // the render reports one preview frame mid-flight before completing.
        fake.OnRender = (req, onProgress) => onProgress(new SwarmGen.Progress(0.5, "data:image/jpeg;base64,zz"));
        var coord = new ChatGenCoordinator(fake.Build());

        await coord.DrainAsync();

        // the warming-up preview was threaded onto the record as a rendering update before done.
        Assert.Contains("data:image/jpeg;base64,zz", fake.PreviewsOf("a"));
        Assert.Equal("done", fake.StatusOf("a"));
    }

    [Fact]
    public async Task DrainAsync_is_single_flight_a_concurrent_drain_no_ops()
    {
        // Gate the first drain inside Render so a second DrainAsync overlaps it; the second must no-op (single
        // GPU), leaving exactly ONE pass over the queue.
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var fake = new FakeDeps(active: "media", queued: new[] { Pending("p", id: "a") });
        fake.RenderHook = async () => { entered.TrySetResult(); await release.Task; };
        var coord = new ChatGenCoordinator(fake.Build());

        var first = coord.DrainAsync();
        await entered.Task;                 // first drain is now inside Render, holding the gate
        await coord.DrainAsync();           // second drain overlaps -> must no-op immediately
        release.SetResult();                // let the first finish
        await first;

        Assert.Single(fake.RenderedPrompts);   // exactly one render across both calls
        Assert.Equal(1, fake.RenderCount);
    }

    // ---- fakes ----

    private static PendingGen Pending(string prompt, string id = "id", string kind = "image", string? model = null,
                                      int count = 1, string? created = null, string? initImage = null, double? strength = null)
        => new(Id: id, Prompt: prompt, Kind: kind, Model: model, Count: count,
               Created: created ?? "2026-06-21T10:00:00Z", Conversation: null, Status: "queued",
               InitImage: initImage, Strength: strength);

    // A recording fake for ChatGenCoordinator.Deps: every side-effecting seam is captured so a test can assert the
    // exact sequence with no GPU/SwarmUI/disk. Defaults render every item successfully.
    private sealed class FakeDeps
    {
        private readonly string _active;
        private readonly IReadOnlyList<PendingGen> _queued;

        public FakeDeps(string active, IReadOnlyList<PendingGen> queued) { _active = active; _queued = queued; }

        public bool SwarmReady { get; set; } = true;
        public bool FlipBack { get; set; } = true;

        // optional render shaping
        public Func<GenRequest, SwarmGen.Outcome>? RenderResult { get; set; }
        public Action<GenRequest, Action<SwarmGen.Progress>>? OnRender { get; set; }
        public Func<Task>? RenderHook { get; set; }

        // recordings
        public List<string> ModeSwitches { get; } = new();
        public int ReadyWaits { get; private set; }
        public List<string> RenderedPrompts { get; } = new();
        public int RenderCount { get; private set; }
        public List<(string artifact, string id, string kind, string prompt)> Sidecars { get; } = new();
        // id -> ordered (status, resultRel, error, preview) writes
        public Dictionary<string, List<(string status, string? resultRel, string? error, string? preview)>> Writes { get; } = new();

        public ChatGenCoordinator.Deps Build() => new()
        {
            GetActiveGroup = _ => Task.FromResult(_active),
            SwitchMode = profile => ModeSwitches.Add(profile),
            WaitForSwarmReady = _ => { ReadyWaits++; return Task.FromResult(SwarmReady); },
            Queued = () => _queued,
            Render = async (req, onProgress, ct) =>
            {
                RenderCount++;
                RenderedPrompts.Add(req.Prompt);
                if (RenderHook is not null) await RenderHook();
                OnRender?.Invoke(req, onProgress);
                return RenderResult is not null ? RenderResult(req) : new SwarmGen.Outcome(true, $@"C:\g\{req.Prompt}.png", "done");
            },
            SetStatus = (id, status, resultRel, error, preview) =>
            {
                if (!Writes.TryGetValue(id, out var list)) Writes[id] = list = new();
                list.Add((status, resultRel, error, preview));
            },
            WriteSidecar = (artifact, id, kind, prompt) => Sidecars.Add((artifact, id, kind, prompt)),
            FlipBackToAgentWhenDone = FlipBack,
        };

        // ---- assertion helpers over the recorded lifecycle writes ----
        private List<(string status, string? resultRel, string? error, string? preview)> W(string id)
            => Writes.TryGetValue(id, out var l) ? l : new();
        public string? StatusOf(string id) => W(id).LastOrDefault().status;
        public string[] TransitionsOf(string id) => W(id).Select(w => w.status).Distinct().ToArray();
        public string? ErrorOf(string id) => W(id).Select(w => w.error).LastOrDefault(e => e is not null);
        public IEnumerable<string?> PreviewsOf(string id) => W(id).Where(w => w.preview is not null).Select(w => w.preview);
        public IEnumerable<string?> DoneResultRels => Writes.Values.SelectMany(l => l).Where(w => w.status == "done").Select(w => w.resultRel);
    }
}
