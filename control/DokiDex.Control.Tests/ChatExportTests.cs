using System;
using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure conversation -> portable markdown formatter (the export download's body). Total + side-effect-free
// (no disk, no model), so the header/turn/role rules + the pathological-thread bounds are locked here, mirroring
// ChatPromptTests / DirectorTests discipline. The endpoint is a thin Results.File over this.
public class ChatExportTests
{
    private static ChatTurn U(string c) => new("user", c, null);
    private static ChatTurn A(string c) => new("assistant", c, null);

    private static Conversation Conv(IReadOnlyList<ChatTurn> msgs, string? persona = "doki",
        string? lorebook = null, string? kbId = null, string created = "2026-06-18T00:00:00.0000000Z",
        string id = "20260618-000000-abcd1234")
        => new(id, persona, lorebook, created, msgs, kbId);

    [Fact]
    public void Header_carries_id_persona_lorebook_kb_created_and_turn_count()
    {
        var md = ChatExport.ToMarkdown(Conv(
            new[] { U("hello"), A("hi there") },
            persona: "Doki", lorebook: "World", kbId: "kb-1"));

        Assert.Contains("# Conversation 20260618-000000-abcd1234", md);
        Assert.Contains("Persona: Doki", md);
        Assert.Contains("Lorebook: World", md);
        Assert.Contains("Knowledge base: kb-1", md);
        Assert.Contains("Created: 2026-06-18T00:00:00.0000000Z", md);
        Assert.Contains("Turns: 2", md);
        Assert.Contains("---", md);
    }

    [Fact]
    public void Null_persona_lorebook_kb_render_as_sensible_defaults()
    {
        var md = ChatExport.ToMarkdown(Conv(new[] { U("x") }, persona: null, lorebook: null, kbId: null));
        Assert.Contains("Persona: default", md);
        Assert.Contains("Lorebook: none", md);
        Assert.Contains("Knowledge base: none", md);
    }

    [Fact]
    public void Turns_render_with_you_and_assistant_labels_in_stored_order_and_verbatim_content()
    {
        var md = ChatExport.ToMarkdown(Conv(new[] { U("first ask"), A("**bold** answer") }));

        var youIdx = md.IndexOf("**You:**", StringComparison.Ordinal);
        var asstIdx = md.IndexOf("**Assistant:**", StringComparison.Ordinal);
        Assert.True(youIdx >= 0 && asstIdx >= 0);
        Assert.True(youIdx < asstIdx);                 // stored order preserved
        Assert.Contains("first ask", md);
        Assert.Contains("**bold** answer", md);        // assistant markdown emitted verbatim (not escaped)
    }

    [Fact]
    public void Unknown_role_renders_as_a_blockquote_not_mislabeled()
    {
        var md = ChatExport.ToMarkdown(Conv(new[] { new ChatTurn("tool", "ran a tool", null) }));
        Assert.Contains("> [tool]", md);
        Assert.DoesNotContain("**You:**", md);
        Assert.DoesNotContain("**Assistant:**", md);
    }

    [Fact]
    public void A_data_url_attachment_is_summarized_not_dumped()
    {
        var huge = "data:image/png;base64," + new string('A', 5000);
        var md = ChatExport.ToMarkdown(Conv(new[] { U(huge) }));
        Assert.Contains("[image attachment omitted]", md);
        Assert.DoesNotContain(new string('A', 5000), md);
    }

    [Fact]
    public void A_pathological_long_turn_is_truncated_with_a_marker()
    {
        var body = new string('x', ChatExport.MaxTurnChars + 5000);
        var md = ChatExport.ToMarkdown(Conv(new[] { A(body) }));
        Assert.Contains("[truncated,", md);
        // the rendered single turn body cannot exceed the cap (+ the short marker), never the full 25k.
        Assert.DoesNotContain(new string('x', ChatExport.MaxTurnChars + 1), md);
    }

    [Fact]
    public void Too_many_turns_keeps_the_most_recent_and_notes_the_omission()
    {
        var n = ChatExport.MaxTurns + 50;
        var msgs = Enumerable.Range(0, n).Select(i => U("turn-" + i)).ToList();
        var md = ChatExport.ToMarkdown(Conv(msgs));

        Assert.Contains("earlier turns omitted", md);
        Assert.Contains("turn-" + (n - 1), md);        // most-recent kept
        Assert.DoesNotContain("turn-0\n", md);         // an early turn dropped
    }

    [Fact]
    public void Overall_output_is_bounded_by_the_byte_cap()
    {
        // many maximal turns would exceed the byte cap without the backstop.
        var msgs = Enumerable.Range(0, ChatExport.MaxTurns)
            .Select(_ => A(new string('y', ChatExport.MaxTurnChars))).ToList();
        var md = ChatExport.ToMarkdown(Conv(msgs));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(md) <= ChatExport.MaxBytes,
            $"export was {System.Text.Encoding.UTF8.GetByteCount(md)} bytes, cap {ChatExport.MaxBytes}");
    }

    // The incremental byte counter (FIX 2) must not fire EARLY: a comfortably under-cap thread renders every turn
    // verbatim with no truncation marker, byte-for-byte the same as before the O(n) refactor.
    [Fact]
    public void An_under_cap_thread_is_rendered_in_full_with_no_byte_cap_marker()
    {
        var msgs = Enumerable.Range(0, 200).Select(i => U("turn-" + i + " body")).ToList();
        var md = ChatExport.ToMarkdown(Conv(msgs));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(md) < ChatExport.MaxBytes);
        Assert.DoesNotContain("output truncated at byte cap", md);
        Assert.DoesNotContain("[truncated,", md);          // no per-turn cap either
        Assert.Contains("turn-0 body", md);                // earliest kept
        Assert.Contains("turn-199 body", md);              // latest kept
    }

    [Fact]
    public void Null_conversation_renders_empty_string()
    {
        Assert.Equal("", ChatExport.ToMarkdown(null!));
    }

    // System.Text.Json deserializes a hand-edited "messages":null into a null Messages despite the non-nullable
    // record positional, and ChatStore.Load only null-checks the whole Conversation — so a corrupt thread can put
    // null Messages in front of ToMarkdown. It must not throw: emit the header (with 0 turns) and stop.
    [Fact]
    public void Null_messages_does_not_throw_and_still_emits_the_header()
    {
        var conv = new Conversation("20260618-000000-abcd1234", "Doki", "World",
            "2026-06-18T00:00:00.0000000Z", null!, "kb-1");
        var md = ChatExport.ToMarkdown(conv);
        Assert.Contains("# Conversation 20260618-000000-abcd1234", md);
        Assert.Contains("Persona: Doki", md);
        Assert.Contains("Turns: 0", md);   // null Messages treated as zero turns
        Assert.Contains("---", md);
    }

    // Same corrupt-thread surface: a JSON array can carry a null element, which deserializes to a null ChatTurn.
    // The null turn must be skipped (not NRE'd) while the surrounding real turns still render.
    [Fact]
    public void A_null_turn_in_messages_is_skipped_not_dereferenced()
    {
        var conv = new Conversation("20260618-000000-abcd1234", "p", null,
            "2026-06-18T00:00:00.0000000Z", new ChatTurn?[] { U("before"), null, A("after") }!, null);
        var md = ChatExport.ToMarkdown(conv);
        Assert.Contains("before", md);
        Assert.Contains("after", md);
        Assert.Contains("Turns: 3", md);   // the null still counts toward the stored length, only its render is skipped
    }
}
