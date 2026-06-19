using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Named cross-conversation KNOWLEDGE-BASE library store: an id-keyed clone of ChatStore (server-generated id,
// never a user filename — so two KBs can share a display name and a rename can't move chunks). Round-trips over
// the real disk store under RepoPaths.Root, creating + cleaning its own kbs/ entry. The attach RESOLUTION
// (a conversation -> its effective kb_id) is the additive, pure core that lets a named KB or the per-conversation
// fallback both flow through the unchanged kb_id-scoped doc pipeline.
public class KbStoreTests
{
    [Fact]
    public void NewKb_generates_a_kb_prefixed_server_id_and_trims_the_name()
    {
        var rec = KbStore.NewKb("  My Workspace  ");
        Assert.False(string.IsNullOrWhiteSpace(rec.Id));   // server-generated, never client-supplied
        Assert.StartsWith("kb-", rec.Id);                  // visually distinct from a conversation id in doc_index.db
        Assert.Equal("My Workspace", rec.Name);            // trimmed
        Assert.False(string.IsNullOrWhiteSpace(rec.Created));
        // the generated id is a legal file stem (no traversal, SafeName-clean)
        Assert.Equal(rec.Id, RecipeStore.SafeName(rec.Id));
    }

    [Fact]
    public void Save_then_load_round_trips_all_fields()
    {
        string? id = null;
        try
        {
            var rec = KbStore.NewKb("research kb zz");
            id = rec.Id;
            Assert.True(KbStore.Save(rec));

            var loaded = KbStore.Load(id);
            Assert.NotNull(loaded);
            Assert.Equal(id, loaded!.Id);
            Assert.Equal("research kb zz", loaded.Name);
            Assert.Equal(rec.Created, loaded.Created);

            Assert.Contains(KbStore.List(), k => k.Id == id);
        }
        finally { if (id is not null) KbStore.Delete(id); }
    }

    [Fact]
    public void Generated_ids_are_unique()
    {
        var a = KbStore.NewKb("k");
        var b = KbStore.NewKb("k");           // SAME display name is allowed — ids still differ
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Name, b.Name);
    }

    [Fact]
    public void List_skips_blank_id_and_orders_by_name_case_insensitively()
    {
        string? id1 = null, id2 = null;
        try
        {
            var b = KbStore.NewKb("zzz beta kb test") with { };
            var a = KbStore.NewKb("aaa alpha kb test") with { };
            id1 = b.Id; id2 = a.Id;
            Assert.True(KbStore.Save(b));
            Assert.True(KbStore.Save(a));

            var listed = KbStore.List().Where(k => k.Id == id1 || k.Id == id2).ToList();
            Assert.Equal(2, listed.Count);
            Assert.Equal(id2, listed[0].Id);   // "aaa..." orders before "zzz..."
        }
        finally { if (id1 is not null) KbStore.Delete(id1); if (id2 is not null) KbStore.Delete(id2); }
    }

    [Fact]
    public void Load_with_an_unsafe_id_returns_null_without_traversal()
    {
        Assert.Null(KbStore.Load("../../secret"));
        Assert.Null(KbStore.Load("a/b"));
        Assert.Null(KbStore.Load(null));
    }

    [Fact]
    public void Delete_removes_the_kb_and_rejects_an_unsafe_id()
    {
        var rec = KbStore.NewKb("delete me kb zz");
        Assert.True(KbStore.Save(rec));
        Assert.True(KbStore.Delete(rec.Id));
        Assert.Null(KbStore.Load(rec.Id));

        Assert.False(KbStore.Delete("../../etc"));   // unsafe id never touches disk
    }

    // ---- the attach RESOLUTION: a conversation -> its effective kb_id ----

    [Fact]
    public void EffectiveKbId_for_an_unattached_conversation_is_its_own_id()
    {
        // the per-conversation KB path, byte-for-byte: KbId null -> the conversation's own id is the kb scope.
        var conv = ChatStore.NewConversation("doki", lorebook: null);
        Assert.Null(conv.KbId);
        Assert.Equal(conv.Id, KbStore.EffectiveKbId(conv));
    }

    [Fact]
    public void EffectiveKbId_for_a_self_keyed_conversation_kb_is_still_its_own_id()
    {
        // after a private-doc attach today, KbId == the conversation's own id — resolution must be identical.
        var conv = ChatStore.NewConversation("doki", lorebook: null);
        conv = conv with { KbId = conv.Id };
        Assert.Equal(conv.Id, KbStore.EffectiveKbId(conv));
    }

    [Fact]
    public void EffectiveKbId_for_a_named_kb_attached_conversation_is_the_named_kb_id()
    {
        // a thread pointed at a NAMED library resolves to the library id (shared across threads) — the addition.
        var conv = ChatStore.NewConversation("doki", lorebook: null);
        conv = conv with { KbId = "kb-20260618-000000-deadbeef" };
        Assert.Equal("kb-20260618-000000-deadbeef", KbStore.EffectiveKbId(conv));
    }

    [Fact]
    public void EffectiveKbId_treats_blank_kbid_as_unattached()
    {
        var conv = ChatStore.NewConversation("doki", lorebook: null) with { KbId = "   " };
        Assert.Equal(conv.Id, KbStore.EffectiveKbId(conv));
    }

    [Fact]
    public void Two_conversations_attached_to_one_named_kb_resolve_to_the_same_scope()
    {
        // proves the shared-library reuse at the resolution layer: both threads' effective kb_id is the SAME named
        // id, so RetrieveDocs(conv.KbId,...) (unchanged) searches one shared doc_index scope for both. No cross-KB
        // leakage is enforced downstream by doc_index's WHERE kb_id=? (a separate kb id never matches).
        var kbId = "kb-20260618-111111-cafef00d";
        var a = ChatStore.NewConversation("p", null) with { KbId = kbId };
        var b = ChatStore.NewConversation("p", null) with { KbId = kbId };
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(kbId, KbStore.EffectiveKbId(a));
        Assert.Equal(kbId, KbStore.EffectiveKbId(b));
    }

    // ---- FIX 3(b): the NEGATIVE no-leak assertion (complements the same-KB positive test above) ----

    [Fact]
    public void Two_different_named_kbs_resolve_to_different_effective_scopes()
    {
        // The shared-library NON-leakage invariant at the resolution layer: two threads on DIFFERENT named libraries
        // must NOT collapse to one scope. If a future EffectiveKbId refactor ever conflated distinct kb-* ids (e.g.
        // mapped both to the conversation id, or truncated the suffix), RetrieveDocs would cross-contaminate the two
        // libraries' chunks — this test fails first. Distinct ids in => distinct scopes out, always.
        var a = ChatStore.NewConversation("p", null) with { KbId = "kb-20260618-111111-aaaaaaaa" };
        var b = ChatStore.NewConversation("p", null) with { KbId = "kb-20260618-222222-bbbbbbbb" };
        Assert.NotEqual(KbStore.EffectiveKbId(a), KbStore.EffectiveKbId(b));
        Assert.Equal("kb-20260618-111111-aaaaaaaa", KbStore.EffectiveKbId(a));
        Assert.Equal("kb-20260618-222222-bbbbbbbb", KbStore.EffectiveKbId(b));
    }

    // ---- FIX 1: the conversation-delete CLEANUP scope (the disk-leak fix, as a pure helper) ----
    // On DELETE /api/chats/{id} the conversation's OWN private per-conversation scope (chunks stored under conv.Id)
    // must ALWAYS be the cleanup target — even when the thread was later pointed at a NAMED library — while a shared
    // named (kb-*) library is NEVER dropped by a conversation delete (only DELETE /api/kbs/{id} drops a named KB).

    [Fact]
    public void CleanupScope_for_an_unattached_conversation_is_its_own_id()
    {
        // A pure per-conversation thread (never attached anywhere): its private chunks live under conv.Id, so that
        // is exactly what a delete must drop.
        var conv = ChatStore.NewConversation("doki", lorebook: null);
        Assert.Null(conv.KbId);
        Assert.Equal(conv.Id, KbStore.ScopeToCleanupOnConversationDelete(conv));
    }

    [Fact]
    public void CleanupScope_for_a_self_keyed_private_conversation_is_its_own_id()
    {
        // After a private-doc attach today KbId == conv.Id; the cleanup target is still conv.Id (the private scope).
        var conv = ChatStore.NewConversation("doki", lorebook: null);
        conv = conv with { KbId = conv.Id };
        Assert.Equal(conv.Id, KbStore.ScopeToCleanupOnConversationDelete(conv));
    }

    [Fact]
    public void CleanupScope_for_a_named_attached_conversation_is_its_OWN_id_NEVER_the_named_kb_id()
    {
        // THE disk-leak fix: a thread that ingested PRIVATE docs (chunks under conv.Id) and was THEN attached to a
        // named library (KbId = kb-*) must STILL have its conv.Id private chunks cleaned on delete — otherwise they
        // orphan in doc_index.db forever. And the shared kb-* library must NEVER be the cleanup target (other threads
        // may still use it; it is dropped only via DELETE /api/kbs/{id}).
        var named = "kb-20260618-333333-deadbeef";
        var conv = ChatStore.NewConversation("doki", lorebook: null) with { KbId = named };

        var target = KbStore.ScopeToCleanupOnConversationDelete(conv);
        Assert.Equal(conv.Id, target);     // the private scope is ALWAYS cleaned
        Assert.NotEqual(named, target);    // the shared named library is NEVER dropped by a conversation delete
    }

    // ---- FIX 2: detach-to-private RESOLUTION (a blank kbId on POST /api/chats/{id}/kb) ----
    // Detaching from a named library must RESTORE the conversation's own private KB *when that scope has docs* (so
    // they are actually retrieved, not just listed), else go to the clean no-KB state.

    [Fact]
    public void DetachResolution_with_private_docs_restores_the_conversation_own_scope()
    {
        // The thread has private docs under conv.Id (from an earlier private attach): detaching from a named library
        // must point KbId at conv.Id so RetrieveDocs(conv.KbId,...) actually injects them — not null (which would
        // list-but-not-retrieve: "RAG looks on but is off").
        var conv = ChatStore.NewConversation("doki", lorebook: null) with { KbId = "kb-20260618-444444-feedface" };
        Assert.Equal(conv.Id, KbStore.ResolveDetachKbId(conv, hasPrivateDocs: true));
    }

    [Fact]
    public void DetachResolution_without_private_docs_is_the_clean_no_KB_state()
    {
        // No private docs under conv.Id: detaching goes fully clean (KbId = null) so RetrieveDocs short-circuits and
        // the no-KB chat path resumes byte-for-byte (no pointless doc_search every turn over an empty scope).
        var conv = ChatStore.NewConversation("doki", lorebook: null) with { KbId = "kb-20260618-444444-feedface" };
        Assert.Null(KbStore.ResolveDetachKbId(conv, hasPrivateDocs: false));
    }
}
