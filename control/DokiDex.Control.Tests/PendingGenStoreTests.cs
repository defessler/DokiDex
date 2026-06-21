using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The persisted pending-gen store: on-disk durable queue (pending-gen/<id>.json) for generate_image tool calls.
// A chat turn runs with the LLM resident on the 32GB GPU; SwarmUI (the renderer) is GPU-exclusive with it, so a
// gen CANNOT render mid-chat. The tool QUEUES a request here; the durable JSON survives the eventual GPU flip to
// media mode. Ids are SERVER-generated (timestamp + short guid => no client path => no traversal). Round-trips
// over the real disk store under RepoPaths.Root, creating + cleaning its own pending-gen/ entry — the same
// pattern ChatStoreTests use.
//
// [Collection("PendingGenStore")] — serialized with ChatToolsTests (the other writer of the shared pending-gen/
// dir) so the two classes don't interleave Enqueue/Delete across xUnit's class-parallelism and flap List counts.
[Collection("PendingGenStore")]
public class PendingGenStoreTests
{
    [Fact]
    public void Enqueue_generates_a_server_id_and_load_round_trips_the_record()
    {
        string? id = null;
        try
        {
            var rec = PendingGenStore.Enqueue("a neon dragon at night", "image", model: "auto", count: 3, conversation: "conv-1");
            id = rec.Id;
            Assert.False(string.IsNullOrWhiteSpace(id));   // server-generated, never client-supplied
            Assert.False(string.IsNullOrWhiteSpace(rec.Created));

            var loaded = PendingGenStore.Load(id);
            Assert.NotNull(loaded);
            Assert.Equal(id, loaded!.Id);
            Assert.Equal("a neon dragon at night", loaded.Prompt);
            Assert.Equal("image", loaded.Kind);
            Assert.Equal("auto", loaded.Model);
            Assert.Equal(3, loaded.Count);
            Assert.Equal("conv-1", loaded.Conversation);

            Assert.Contains(PendingGenStore.List(), p => p.Id == id);
        }
        finally { if (id is not null) PendingGenStore.Delete(id); }
    }

    [Fact]
    public void List_is_newest_first()
    {
        string? a = null, b = null;
        try
        {
            var first = PendingGenStore.Enqueue("first", "image", null, 1, null);
            a = first.Id;
            // Distinct ids even within the same second (the guid suffix differentiates), but List orders by the
            // Created timestamp string descending — assert the most-recently-enqueued appears before the earlier one.
            var second = PendingGenStore.Enqueue("second", "image", null, 1, null);
            b = second.Id;

            var list = PendingGenStore.List().Where(p => p.Id == a || p.Id == b).ToList();
            Assert.Equal(2, list.Count);
            // newest-first: the later Created sorts ahead
            Assert.True(string.CompareOrdinal(list[0].Created, list[1].Created) >= 0);
        }
        finally { if (a is not null) PendingGenStore.Delete(a); if (b is not null) PendingGenStore.Delete(b); }
    }

    [Fact]
    public void Generated_ids_are_unique()
    {
        var a = PendingGenStore.Enqueue("p", "image", null, 1, null);
        var b = PendingGenStore.Enqueue("p", "image", null, 1, null);
        try { Assert.NotEqual(a.Id, b.Id); }
        finally { PendingGenStore.Delete(a.Id); PendingGenStore.Delete(b.Id); }
    }

    [Fact]
    public void Load_with_an_unsafe_id_returns_null_without_traversal()
    {
        Assert.Null(PendingGenStore.Load("../../secret"));
        Assert.Null(PendingGenStore.Load("a/b"));
        Assert.Null(PendingGenStore.Load(".."));
    }

    [Fact]
    public void Delete_removes_the_pending_gen()
    {
        var rec = PendingGenStore.Enqueue("p", "image", null, 1, null);
        Assert.True(PendingGenStore.Delete(rec.Id));
        Assert.Null(PendingGenStore.Load(rec.Id));
    }

    [Fact]
    public void Enqueue_starts_a_pending_gen_in_the_queued_status_with_no_result()
    {
        // The render round-trip (P1) is a lifecycle: queued -> rendering -> done/failed. A freshly-queued gen
        // starts "queued" with no result/error so the deferred renderer + the chat SPA can poll its progress.
        var rec = PendingGenStore.Enqueue("p", "image", null, 1, "conv-1");
        try
        {
            Assert.Equal("queued", rec.Status);
            Assert.Null(rec.ResultRel);
            Assert.Null(rec.Error);
            Assert.Equal("queued", PendingGenStore.Load(rec.Id)!.Status);   // durable
        }
        finally { PendingGenStore.Delete(rec.Id); }
    }

    [Fact]
    public void SetStatus_moves_a_pending_gen_through_rendering_to_done_with_a_result_path()
    {
        var rec = PendingGenStore.Enqueue("p", "image", null, 1, "conv-9");
        try
        {
            Assert.Equal("rendering", PendingGenStore.SetStatus(rec.Id, "rendering")!.Status);

            var done = PendingGenStore.SetStatus(rec.Id, "done", resultRel: "2026/06/img_001.png");
            Assert.Equal("done", done!.Status);
            Assert.Equal("2026/06/img_001.png", done.ResultRel);
            Assert.Equal("conv-9", done.Conversation);   // backlink + identity preserved across the rewrite
            Assert.Equal(rec.Id, done.Id);
            Assert.Equal("done", PendingGenStore.Load(rec.Id)!.Status);   // durable
        }
        finally { PendingGenStore.Delete(rec.Id); }
    }

    [Fact]
    public void SetStatus_records_a_failure_message()
    {
        var rec = PendingGenStore.Enqueue("p", "image", null, 1, null);
        try
        {
            var failed = PendingGenStore.SetStatus(rec.Id, "failed", error: "SwarmUI not reachable");
            Assert.Equal("failed", failed!.Status);
            Assert.Equal("SwarmUI not reachable", failed.Error);
            Assert.Null(failed.ResultRel);
        }
        finally { PendingGenStore.Delete(rec.Id); }
    }

    [Fact]
    public void SetStatus_on_an_unknown_or_unsafe_id_returns_null()
    {
        Assert.Null(PendingGenStore.SetStatus("nonexistent-id-xyz", "done"));
        Assert.Null(PendingGenStore.SetStatus("../escape", "done"));   // traversal-guarded like Load/Delete
    }

    // ---- round-trip seam for the chat->media round-trip build (edit_image + the coordinator + inline surfacing) ----

    [Fact]
    public void Enqueue_persists_init_image_and_strength_for_edits()
    {
        var rec = PendingGenStore.Enqueue("refine", "image", null, 1, "conv-e", initImage: "2026/06/src.png", strength: 0.55);
        try
        {
            var loaded = PendingGenStore.Load(rec.Id)!;
            Assert.Equal("2026/06/src.png", loaded.InitImage);   // edit_image source survives the disk round-trip
            Assert.Equal(0.55, loaded.Strength);
        }
        finally { PendingGenStore.Delete(rec.Id); }
    }

    [Fact]
    public void SetStatus_threads_an_in_flight_preview_then_clears_it_on_done()
    {
        var rec = PendingGenStore.Enqueue("p", "image", null, 1, null);
        try
        {
            var r = PendingGenStore.SetStatus(rec.Id, "rendering", preview: "data:image/jpeg;base64,zz");
            Assert.Equal("rendering", r!.Status);
            Assert.Equal("data:image/jpeg;base64,zz", r.Preview);
            var done = PendingGenStore.SetStatus(rec.Id, "done", resultRel: "x.png");
            Assert.Null(done!.Preview);   // the final image replaces the warming-up preview (no stale lingering)
        }
        finally { PendingGenStore.Delete(rec.Id); }
    }

    [Fact]
    public void FilterQueued_keeps_only_queued_oldest_first()
    {
        var all = new[]
        {
            new PendingGen("c", "p", "image", null, 1, "2026-06-21T10:02:00Z", null, Status: "done"),
            new PendingGen("a", "p", "image", null, 1, "2026-06-21T10:00:00Z", null, Status: "queued"),
            new PendingGen("b", "p", "image", null, 1, "2026-06-21T10:01:00Z", null, Status: "queued"),
            new PendingGen("d", "p", "image", null, 1, "2026-06-21T10:03:00Z", null, Status: "rendering"),
        };
        Assert.Equal(new[] { "a", "b" }, PendingGenStore.FilterQueued(all).Select(p => p.Id).ToArray());   // queued only, FIFO
        Assert.Empty(PendingGenStore.FilterQueued(all.Where(p => p.Status != "queued")));
    }
}
