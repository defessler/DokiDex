using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The chat-thread surfacing of the render round-trip: GET /api/chat/pending-gens?conversation=<id> returns only
// the pending gens belonging to ONE chat thread so the SPA can show their lifecycle (queued -> rendering ->
// done/failed) inline in the originating conversation. The endpoint is a thin wrapper over PendingGenStore.List()
// (live-disk, exercised by PendingGenStoreTests); the narrowing is the pure ChatGenEndpoints.FilterByConversation
// core, unit-tested here over an in-memory PendingGen[] with NO disk/GPU — the same seam style as FilterQueued.
public class ChatGenEndpointsTests
{
    private static PendingGen[] Sample() => new[]
    {
        new PendingGen("g1", "a", "image", null, 1, "2026-06-21T10:00:00Z", "conv-1", Status: "queued"),
        new PendingGen("g2", "b", "image", null, 1, "2026-06-21T10:01:00Z", "conv-2", Status: "rendering"),
        new PendingGen("g3", "c", "image", null, 1, "2026-06-21T10:02:00Z", "conv-1", Status: "done", ResultRel: "x.png"),
        new PendingGen("g4", "d", "image", null, 1, "2026-06-21T10:03:00Z", null,     Status: "queued"),
    };

    [Fact]
    public void FilterByConversation_keeps_only_the_matching_thread_in_source_order()
    {
        // conv-1 owns g1 + g3 (g2 is conv-2, g4 has no conversation) — and the source (newest-first) order is kept.
        Assert.Equal(new[] { "g1", "g3" }, ChatGenEndpoints.FilterByConversation(Sample(), "conv-1").Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "g2" }, ChatGenEndpoints.FilterByConversation(Sample(), "conv-2").Select(p => p.Id).ToArray());
    }

    [Fact]
    public void FilterByConversation_returns_empty_for_an_unknown_thread()
    {
        Assert.Empty(ChatGenEndpoints.FilterByConversation(Sample(), "conv-nope"));
    }

    [Fact]
    public void FilterByConversation_never_leaks_other_threads_for_a_blank_or_null_conversation()
    {
        // A fresh thread has no server id yet => nothing to show; we must NOT fall back to "all" (that would leak
        // every other conversation's queued work — including the conversation-less g4 — into the empty thread).
        Assert.Empty(ChatGenEndpoints.FilterByConversation(Sample(), null));
        Assert.Empty(ChatGenEndpoints.FilterByConversation(Sample(), ""));
        Assert.Empty(ChatGenEndpoints.FilterByConversation(Sample(), "   "));
    }

    [Fact]
    public void FilterByConversation_matches_the_id_exactly_case_sensitive()
    {
        // Conversation ids are server-generated opaque tokens; a case-folded or prefix match would cross threads.
        Assert.Empty(ChatGenEndpoints.FilterByConversation(Sample(), "CONV-1"));
        Assert.Empty(ChatGenEndpoints.FilterByConversation(Sample(), "conv-"));
    }
}
