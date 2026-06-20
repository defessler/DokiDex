using System;
using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Pure thread-mutation cores for chat interactivity (regenerate / edit / delete). Each operates on an
// IReadOnlyList<ChatTurn> and returns a NEW list — no disk, no LLM — so they unit-test exactly like ChatPrompt /
// ChatSearch. Index == the position in conv.Messages (0-based), matching the SPA's _chatMsgs index. All three are
// TOTAL: an out-of-range index returns the input unchanged (graceful endpoints map that to a no-op, never a crash).
public class ChatEditTests
{
    private static ChatTurn U(string c) => new("user", c, null);
    private static ChatTurn A(string c) => new("assistant", c, null);

    private static IReadOnlyList<ChatTurn> Thread(params ChatTurn[] turns) => turns;

    // ---- TruncateToTurn: keeps [0..index), drops index and everything after ----------------------------------

    [Fact]
    public void Truncate_keeps_everything_strictly_before_index()
    {
        var t = Thread(U("q1"), A("a1"), U("q2"), A("a2"));
        var r = ChatEdit.TruncateToTurn(t, 2);
        Assert.Equal(2, r.Count);
        Assert.Equal("q1", r[0].Content);
        Assert.Equal("a1", r[1].Content);
    }

    [Fact]
    public void Truncate_at_zero_yields_empty()
    {
        var t = Thread(U("q1"), A("a1"));
        Assert.Empty(ChatEdit.TruncateToTurn(t, 0));
    }

    [Fact]
    public void Truncate_at_count_is_a_no_op()
    {
        var t = Thread(U("q1"), A("a1"));
        var r = ChatEdit.TruncateToTurn(t, t.Count);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void Truncate_out_of_range_returns_input_unchanged()
    {
        var t = Thread(U("q1"), A("a1"));
        Assert.Same(t, ChatEdit.TruncateToTurn(t, -1));
        Assert.Same(t, ChatEdit.TruncateToTurn(t, 99));
    }

    [Fact]
    public void Truncate_empty_thread_is_unchanged()
    {
        var t = Thread();
        Assert.Empty(ChatEdit.TruncateToTurn(t, 0));
    }

    // ---- EditTurn: user-only; replaces content + drops everything after -------------------------------------

    [Fact]
    public void Edit_replaces_user_content_and_drops_the_stale_reply_and_later_turns()
    {
        var t = Thread(U("q1"), A("a1"), U("q2"), A("a2"));
        var r = ChatEdit.EditTurn(t, 0, "q1-edited");
        Assert.Single(r);
        Assert.Equal("user", r[0].Role);
        Assert.Equal("q1-edited", r[0].Content);
    }

    [Fact]
    public void Edit_a_middle_user_turn_keeps_the_prefix_and_drops_after()
    {
        var t = Thread(U("q1"), A("a1"), U("q2"), A("a2"));
        var r = ChatEdit.EditTurn(t, 2, "q2-edited");
        Assert.Equal(3, r.Count);
        Assert.Equal("q1", r[0].Content);
        Assert.Equal("a1", r[1].Content);
        Assert.Equal("user", r[2].Role);
        Assert.Equal("q2-edited", r[2].Content);
    }

    [Fact]
    public void Edit_on_an_assistant_turn_is_rejected_unchanged()
    {
        var t = Thread(U("q1"), A("a1"));
        Assert.Same(t, ChatEdit.EditTurn(t, 1, "nope"));
    }

    [Fact]
    public void Edit_out_of_range_returns_input_unchanged()
    {
        var t = Thread(U("q1"), A("a1"));
        Assert.Same(t, ChatEdit.EditTurn(t, -1, "x"));
        Assert.Same(t, ChatEdit.EditTurn(t, 5, "x"));
    }

    [Fact]
    public void Edit_preserves_the_user_timestamp()
    {
        var t = Thread(new ChatTurn("user", "q1", "2026-01-01T00:00:00Z"), A("a1"));
        var r = ChatEdit.EditTurn(t, 0, "q1-edited");
        Assert.Equal("2026-01-01T00:00:00Z", r[0].Ts);
    }

    // ---- DeleteTurn: user drops its following assistant; assistant drops only itself ------------------------

    [Fact]
    public void Delete_a_user_turn_drops_its_paired_assistant_reply()
    {
        var t = Thread(U("q1"), A("a1"), U("q2"), A("a2"));
        var r = ChatEdit.DeleteTurn(t, 0);
        Assert.Equal(2, r.Count);
        Assert.Equal("q2", r[0].Content);
        Assert.Equal("a2", r[1].Content);
    }

    [Fact]
    public void Delete_an_assistant_turn_drops_only_itself()
    {
        var t = Thread(U("q1"), A("a1"), U("q2"), A("a2"));
        var r = ChatEdit.DeleteTurn(t, 3);
        Assert.Equal(3, r.Count);
        Assert.Equal("q1", r[0].Content);
        Assert.Equal("a1", r[1].Content);
        Assert.Equal("q2", r[2].Content);
    }

    [Fact]
    public void Delete_a_trailing_user_turn_with_no_reply_drops_only_it()
    {
        var t = Thread(U("q1"), A("a1"), U("q2"));   // q2 has no assistant reply yet
        var r = ChatEdit.DeleteTurn(t, 2);
        Assert.Equal(2, r.Count);
        Assert.Equal("q1", r[0].Content);
        Assert.Equal("a1", r[1].Content);
    }

    [Fact]
    public void Delete_a_user_turn_followed_by_another_user_turn_drops_only_it()
    {
        // Defensive: if the turn after a user is NOT an assistant, the pairing rule does not fire.
        var t = Thread(U("q1"), U("q2"), A("a2"));
        var r = ChatEdit.DeleteTurn(t, 0);
        Assert.Equal(2, r.Count);
        Assert.Equal("q2", r[0].Content);
        Assert.Equal("a2", r[1].Content);
    }

    [Fact]
    public void Delete_out_of_range_returns_input_unchanged()
    {
        var t = Thread(U("q1"), A("a1"));
        Assert.Same(t, ChatEdit.DeleteTurn(t, -1));
        Assert.Same(t, ChatEdit.DeleteTurn(t, 7));
    }

    [Fact]
    public void Delete_on_an_empty_thread_is_unchanged()
    {
        var t = Thread();
        Assert.Same(t, ChatEdit.DeleteTurn(t, 0));
    }

    // ---- LastUserTurnIndex: the regenerate anchor (scan from end for Role=="user") --------------------------

    [Fact]
    public void LastUserTurnIndex_finds_the_trailing_user_turn_before_the_final_assistant()
    {
        var t = Thread(U("q1"), A("a1"), U("q2"), A("a2"));
        Assert.Equal(2, ChatEdit.LastUserTurnIndex(t));
    }

    [Fact]
    public void LastUserTurnIndex_returns_negative_one_when_there_is_no_user_turn()
    {
        Assert.Equal(-1, ChatEdit.LastUserTurnIndex(Thread(A("a1"))));
        Assert.Equal(-1, ChatEdit.LastUserTurnIndex(Thread()));
    }
}
