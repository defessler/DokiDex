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
}
