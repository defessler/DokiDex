using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
}
