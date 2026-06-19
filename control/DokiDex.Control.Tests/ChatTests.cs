using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The persona-chat orchestrator's branching logic, isolated so it's testable without a GPU/network. Two seams:
//   • SelectHistory — the pure choice between the persisted transcript and a stateless caller's supplied turns;
//   • the empty-message guard in SendAsync — reachable with NO LLM (it returns before any network call), so a
//     blank message yields Ok=false + the canonical "empty message" without ever touching :8080.
public class ChatTests
{
    private static ChatTurn U(string c) => new("user", c, null);
    private static ChatTurn A(string c) => new("assistant", c, null);

    private static Conversation Conv(params ChatTurn[] messages)
        => new(Id: "test-id", Persona: null, Lorebook: null, Created: "2026-06-18T00:00:00Z", Messages: messages.ToList());

    [Fact]
    public void SelectHistory_prefers_the_persisted_conversation_when_it_has_messages()
    {
        var conv = Conv(U("stored a"), A("stored b"));
        var supplied = new List<ChatTurn> { U("supplied a") };

        var picked = Chat.SelectHistory(conv, supplied);

        Assert.Same(conv.Messages, picked);   // the persisted transcript wins, supplied is ignored
    }

    [Fact]
    public void SelectHistory_falls_back_to_supplied_when_the_persisted_conversation_is_empty()
    {
        var conv = Conv();   // fresh thread, no stored turns
        var supplied = new List<ChatTurn> { U("supplied a"), A("supplied b") };

        var picked = Chat.SelectHistory(conv, supplied);

        Assert.Same(supplied, picked);   // a stateless caller's transcript seeds history
    }

    [Fact]
    public void SelectHistory_returns_empty_when_the_conversation_is_empty_and_nothing_supplied()
    {
        var conv = Conv();

        var picked = Chat.SelectHistory(conv, null);

        Assert.Empty(picked);   // no persisted, no supplied => empty (not null)
    }

    [Fact]
    public async System.Threading.Tasks.Task SendAsync_rejects_a_blank_message_without_any_network_call()
    {
        // A whitespace-only message hits the guard before the LLM is ever contacted (no :8080 dependency here).
        var r = await Chat.SendAsync(new ChatRequest(null, null, "  ", null), null, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal("empty message", r.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ActivateLore_returns_null_when_the_card_has_no_lorebook(string? lorebookName)
    {
        // No lorebook name => no injection and NO disk touched, so ChatPrompt.Build keeps its exact pre-P3 output.
        var history = new List<ChatTurn> { U("the dragon roared") };
        Assert.Null(Chat.ActivateLore(lorebookName, history, "tell me more"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RetrieveDocs_returns_null_when_there_is_no_attached_kb(string? kbId)
    {
        // No KB id => no retrieval and NO sidecar/process touched, so ChatPrompt.Build keeps the no-KB output
        // byte-for-byte (the same degrade contract code_search has — a conversation with no docs is unchanged).
        Assert.Null(await Chat.RetrieveDocs(kbId, "anything", CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RetrieveDocs_returns_null_for_a_blank_user_message(string? message)
    {
        // A blank turn can't be retrieved against; short-circuit to null (no injection) without touching the sidecar.
        Assert.Null(await Chat.RetrieveDocs("conv-123", message!, CancellationToken.None));
    }

    [Fact]
    public void VisionModel_forces_the_Vision_tier_when_an_image_is_attached()
    {
        // P5 crux: a resolved (non-empty) image data URL overrides whatever speed tier was requested.
        Assert.Equal(LlmTiers.Vision, Chat.VisionModel("data:image/png;base64,AAAA", LlmTiers.Fast));
        Assert.Equal(LlmTiers.Vision, Chat.VisionModel("data:image/png;base64,AAAA", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void VisionModel_returns_the_requested_model_verbatim_when_there_is_no_image(string? imageDataUrl)
    {
        // No/unresolvable image => the originally requested model is left untouched (text-only on the asked tier).
        Assert.Equal(LlmTiers.Quality, Chat.VisionModel(imageDataUrl, LlmTiers.Quality));
        Assert.Null(Chat.VisionModel(imageDataUrl, null));
    }

    // ---- FIX 1 (default-KB orphan/wrong-pill): the send + stream responses must CARRY the conversation's effective
    //      KbId so the SPA can refresh _chatKbId after a fresh send that auto-attached the GLOBAL default KB (and
    //      thus hide the private-doc box + show the right pill). These lock the wire contract the SPA reads; the
    //      SPA refresh itself is verified by the build+embed. ----

    [Fact]
    public void Result_carries_an_optional_KbId_defaulting_to_null_for_the_no_kb_path()
    {
        // The no-KB / no-default path: a Result built without a KbId reports null, so the SPA reads null and keeps
        // the "private to this chat" box — byte-for-byte with the pre-FIX behavior (the field is purely additive).
        var noKb = new Chat.Result(true, "conv-1", "hi", null);
        Assert.Null(noKb.KbId);

        // A fresh send that auto-attached the default library reports that kb-* id, so the SPA snaps to the library
        // scope (hides the private-doc box, shows "library: <name>") exactly as an explicit named attach.
        var withKb = new Chat.Result(true, "conv-1", "hi", null, KbId: "kb-20260618-aaaa");
        Assert.Equal("kb-20260618-aaaa", withKb.KbId);
    }

    [Fact]
    public void StreamEvent_Meta_carries_the_conversation_id_and_an_optional_kbId()
    {
        // The leading stream meta frame hands the SPA the conversation id AND its effective kbId up front (before any
        // token), so a fresh streamed send can refresh _chatKbId from the meta (FIX 1). kbId defaults to null.
        var metaNoKb = Chat.StreamEvent.Meta("conv-9");
        Assert.True(metaNoKb.IsMeta);
        Assert.Equal("conv-9", metaNoKb.ConversationId);
        Assert.Null(metaNoKb.KbId);

        var metaWithKb = Chat.StreamEvent.Meta("conv-9", "kb-20260618-bbbb");
        Assert.Equal("kb-20260618-bbbb", metaWithKb.KbId);

        // A token event is unchanged: not meta, carries the delta, no kbId.
        var tok = Chat.StreamEvent.Token("hello");
        Assert.False(tok.IsMeta);
        Assert.Equal("hello", tok.Delta);
        Assert.Null(tok.KbId);
    }
}
