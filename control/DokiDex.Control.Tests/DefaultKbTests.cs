using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The DEFAULT/GLOBAL KB follow-up (v0.16 polish): a single named KB can be marked the DEFAULT so every NEW
// conversation auto-attaches to it — your main project docs ride along in every fresh chat. Two seams:
//   • DefaultKbStore.Get()/Set() over <root>/kbs/_default.json — a tiny GLOBAL settings entry (NOT a bool on
//     KbRecord), so "exactly one default" is an atomic, drift-free invariant and KbRecord's on-disk shape is
//     untouched (every existing KbStore/ChatStore test stays byte-for-byte). Get() RESOLVE-VALIDATEs: a dangling
//     default (the named KB was deleted) silently degrades to no-default rather than pointing chats at an empty
//     scope.
//   • Chat.ApplyDefaultKb(fresh) — the PURE pickup applied ONLY to a freshly-created thread (the right side of
//     the `?? NewConversation` in Chat.cs). Null default => fresh is returned UNCHANGED (KbId stays null), so the
//     no-default path is byte-for-byte (RetrieveDocs(null) short-circuits exactly as today).
public class DefaultKbTests
{
    // ---- DefaultKbStore: set / get / clear, over the real disk store, cleaning up after itself ----

    [Fact]
    public void Get_is_null_when_no_default_is_set_the_byte_for_byte_no_default_state()
    {
        // Clearing leaves NO default file, so Get() is null — the today's-behavior baseline a fresh thread relies on.
        DefaultKbStore.Set(null);
        Assert.Null(DefaultKbStore.Get());
    }

    [Fact]
    public void Set_then_Get_round_trips_a_real_kb_id()
    {
        string? id = null;
        var prior = DefaultKbStore.Get();
        try
        {
            var rec = KbStore.NewKb("default kb zz");
            id = rec.Id;
            Assert.True(KbStore.Save(rec));

            Assert.True(DefaultKbStore.Set(id));
            Assert.Equal(id, DefaultKbStore.Get());
        }
        finally
        {
            DefaultKbStore.Set(prior);   // restore whatever was there before (don't clobber a real user default)
            if (id is not null) KbStore.Delete(id);
        }
    }

    [Fact]
    public void Set_rejects_a_kb_id_that_does_not_exist()
    {
        var prior = DefaultKbStore.Get();
        try
        {
            // Set validates KbStore.Load(kbId) != null: you can't make a non-existent / unsafe id the default.
            Assert.False(DefaultKbStore.Set("kb-does-not-exist-00000000"));
            Assert.False(DefaultKbStore.Set("../../etc"));
        }
        finally { DefaultKbStore.Set(prior); }
    }

    [Fact]
    public void Set_empty_or_null_clears_the_default()
    {
        string? id = null;
        var prior = DefaultKbStore.Get();
        try
        {
            var rec = KbStore.NewKb("clearable kb zz");
            id = rec.Id;
            KbStore.Save(rec);
            DefaultKbStore.Set(id);
            Assert.Equal(id, DefaultKbStore.Get());

            Assert.True(DefaultKbStore.Set(""));     // "" clears
            Assert.Null(DefaultKbStore.Get());

            DefaultKbStore.Set(id);
            Assert.True(DefaultKbStore.Set(null));   // null clears
            Assert.Null(DefaultKbStore.Get());
        }
        finally
        {
            DefaultKbStore.Set(prior);
            if (id is not null) KbStore.Delete(id);
        }
    }

    [Fact]
    public void Get_returns_null_when_the_default_kb_was_deleted_resolve_validate()
    {
        // THE dangling-default rule: set a default, then DELETE the underlying KB. Get() must RESOLVE-VALIDATE and
        // return null (the named scope no longer KbStore.Loads) so new chats silently degrade to no-default rather
        // than pointing at an empty/missing scope — mirrors the DELETE /kbs graceful-empty rule.
        var prior = DefaultKbStore.Get();
        try
        {
            var rec = KbStore.NewKb("soon deleted default zz");
            KbStore.Save(rec);
            Assert.True(DefaultKbStore.Set(rec.Id));
            Assert.Equal(rec.Id, DefaultKbStore.Get());

            KbStore.Delete(rec.Id);   // the library is gone; the _default.json still names it
            Assert.Null(DefaultKbStore.Get());   // resolve-validate degrades the dangling default to null
        }
        finally { DefaultKbStore.Set(prior); }
    }

    // ---- Chat.ApplyDefaultKb: the PURE pickup (default applies to a NEW thread only; null-default is a no-op) ----

    [Fact]
    public void ApplyDefaultKb_with_no_default_leaves_a_fresh_thread_unchanged_byte_for_byte()
    {
        // The byte-for-byte invariant: with NO default, ApplyDefaultKb returns `fresh` UNCHANGED (KbId still the
        // record default null), so RetrieveDocs(null) short-circuits exactly as today.
        var prior = DefaultKbStore.Get();
        try
        {
            DefaultKbStore.Set(null);
            var fresh = ChatStore.NewConversation("doki", lorebook: null);
            Assert.Null(fresh.KbId);

            var applied = Chat.ApplyDefaultKb(fresh);
            Assert.Null(applied.KbId);            // unchanged — no default to apply
            Assert.Equal(fresh.Id, applied.Id);   // same thread
        }
        finally { DefaultKbStore.Set(prior); }
    }

    [Fact]
    public void ApplyDefaultKb_with_a_default_points_a_new_thread_at_the_default_id()
    {
        string? id = null;
        var prior = DefaultKbStore.Get();
        try
        {
            var rec = KbStore.NewKb("applies-to-new kb zz");
            id = rec.Id;
            KbStore.Save(rec);
            DefaultKbStore.Set(id);

            var fresh = ChatStore.NewConversation("doki", lorebook: null);
            Assert.Null(fresh.KbId);

            var applied = Chat.ApplyDefaultKb(fresh);
            Assert.Equal(id, applied.KbId);   // a NEW thread auto-attaches to the default KB
        }
        finally
        {
            DefaultKbStore.Set(prior);
            if (id is not null) KbStore.Delete(id);
        }
    }

    [Fact]
    public void ApplyDefaultKb_never_repoints_a_loaded_existing_thread()
    {
        // ApplyDefaultKb is applied ONLY to the RIGHT side of `Load(...) ?? NewConversation(...)`, i.e. only to a
        // freshly-created thread. A LOADED existing thread (with its own KbId, or a deliberately-detached null) is
        // NEVER re-pointed — this test guards that ApplyDefaultKb itself, given an already-attached thread, leaves
        // a pre-set KbId intact rather than overwriting it with the default (the helper only fills a fresh thread,
        // but even called on an attached one it must not clobber an explicit choice).
        string? id = null;
        var prior = DefaultKbStore.Get();
        try
        {
            var rec = KbStore.NewKb("default-not-clobbering zz");
            id = rec.Id;
            KbStore.Save(rec);
            DefaultKbStore.Set(id);

            // a thread already pointed at its OWN private scope must keep it, not get the default forced on.
            var attached = ChatStore.NewConversation("doki", lorebook: null);
            attached = attached with { KbId = attached.Id };
            var applied = Chat.ApplyDefaultKb(attached);
            Assert.Equal(attached.Id, applied.KbId);   // the explicit attach is preserved, default does NOT win
        }
        finally
        {
            DefaultKbStore.Set(prior);
            if (id is not null) KbStore.Delete(id);
        }
    }
}
