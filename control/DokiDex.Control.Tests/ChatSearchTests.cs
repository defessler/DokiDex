using System;
using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure chat-history match/snippet core (a substring scan over message CONTENT — NOT RAG/embedding). Total +
// side-effect-free over a hand-built thread list, so the case-insensitivity / first-hit snippet / newest-first /
// bound rules are locked here. The /api/chats/search endpoint is a thin Run(ChatStore.List(), q) over this.
public class ChatSearchTests
{
    private static ChatTurn U(string c) => new("user", c, null);
    private static ChatTurn A(string c) => new("assistant", c, null);

    private static Conversation Conv(string id, string? persona, params ChatTurn[] msgs)
        => new(id, persona, null, "2026-06-18T00:00:00.0000000Z", msgs, null);

    [Fact]
    public void Empty_or_whitespace_query_returns_no_hits()
    {
        var convs = new[] { Conv("a", "p", U("hello world")) };
        Assert.Empty(ChatSearch.Run(convs, ""));
        Assert.Empty(ChatSearch.Run(convs, "   "));
        Assert.Empty(ChatSearch.Run(convs, null));
    }

    [Fact]
    public void A_query_shorter_than_the_minimum_returns_no_hits()
    {
        var convs = new[] { Conv("a", "p", U("banana")) };
        Assert.Empty(ChatSearch.Run(convs, new string('a', ChatSearch.MinQueryLen - 1)));
    }

    [Fact]
    public void Matches_only_the_threads_whose_content_contains_the_query()
    {
        var convs = new[]
        {
            Conv("hit", "p", U("the quick brown fox")),
            Conv("miss", "p", U("nothing relevant here")),
        };
        var hits = ChatSearch.Run(convs, "brown");
        Assert.Single(hits);
        Assert.Equal("hit", hits[0].Id);
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        var convs = new[] { Conv("a", "p", A("The Mitochondria is the POWERHOUSE")) };
        var hits = ChatSearch.Run(convs, "powerhouse");
        Assert.Single(hits);
        Assert.Contains("POWERHOUSE", hits[0].Snippet);
    }

    [Fact]
    public void Snippet_is_a_single_line_window_around_the_first_hit_with_ellipses()
    {
        var left = new string('L', 300);
        var right = new string('R', 300);
        var content = left + " NEEDLE\twith\nnewlines " + right;
        var hits = ChatSearch.Run(new[] { Conv("a", "p", U(content)) }, "NEEDLE");

        Assert.Single(hits);
        var s = hits[0].Snippet;
        Assert.Contains("NEEDLE", s);
        Assert.DoesNotContain("\n", s);                 // collapsed to single line
        Assert.DoesNotContain("\t", s);
        Assert.StartsWith("…", s);                       // clipped on the left
        Assert.EndsWith("…", s);                         // clipped on the right
        Assert.True(s.Length <= ChatSearch.MaxSnippetLen + 2);   // + the two ellipsis sentinels
    }

    [Fact]
    public void Hit_surfaces_id_and_a_persona_label()
    {
        var hits = ChatSearch.Run(new[] { Conv("xyz", "Doki", U("find me")) }, "find");
        Assert.Equal("xyz", hits[0].Id);
        Assert.Equal("Doki", hits[0].Persona);
    }

    [Fact]
    public void Result_order_mirrors_input_order_which_is_newest_first()
    {
        var convs = new[]
        {
            Conv("newest", "p", U("apple")),
            Conv("older",  "p", U("apple")),
        };
        var hits = ChatSearch.Run(convs, "apple");
        Assert.Equal(new[] { "newest", "older" }, hits.Select(h => h.Id).ToArray());
    }

    [Fact]
    public void Result_count_is_bounded()
    {
        var convs = Enumerable.Range(0, ChatSearch.MaxResults + 25)
            .Select(i => Conv("c" + i, "p", U("match here"))).ToList();
        var hits = ChatSearch.Run(convs, "match");
        Assert.Equal(ChatSearch.MaxResults, hits.Count);
    }

    [Fact]
    public void First_matching_turn_is_the_one_snippetted()
    {
        var convs = new[]
        {
            Conv("a", "p", U("no hit here"), A("FIRST cat appears"), A("SECOND cat appears")),
        };
        var hits = ChatSearch.Run(convs, "cat");
        Assert.Single(hits);
        Assert.Contains("FIRST", hits[0].Snippet);
        Assert.DoesNotContain("SECOND", hits[0].Snippet);
    }

    [Fact]
    public void A_thread_with_null_or_empty_messages_does_not_throw_and_simply_misses()
    {
        var convs = new[]
        {
            new Conversation("empty", "p", null, "2026-06-18T00:00:00.0000000Z",
                new List<ChatTurn>(), null),
        };
        Assert.Empty(ChatSearch.Run(convs, "anything"));
    }

    // A corrupt thread can deserialize "messages":null into a null Messages (System.Text.Json ignores the
    // non-nullable positional), and ChatStore.List only null-checks the whole Conversation. Such a thread must be
    // tolerated (not matched, no NRE) while the valid threads around it still produce their hits.
    [Fact]
    public void A_conversation_with_null_messages_is_tolerated_and_the_valid_ones_still_match()
    {
        var convs = new[]
        {
            new Conversation("corrupt", "p", null, "2026-06-18T00:00:00.0000000Z", null!, null),
            Conv("good", "p", U("the quick brown fox")),
        };
        var hits = ChatSearch.Run(convs, "brown");
        Assert.Single(hits);
        Assert.Equal("good", hits[0].Id);
    }

    // Same surface, finer-grained: a null ELEMENT inside Messages (a null array entry) must be skipped, not
    // dereferenced, while a real turn in the same thread still matches.
    [Fact]
    public void A_null_turn_element_is_tolerated_and_a_real_turn_still_matches()
    {
        var convs = new[]
        {
            new Conversation("a", "p", null, "2026-06-18T00:00:00.0000000Z",
                new ChatTurn?[] { null, U("the quick brown fox") }!, null),
        };
        var hits = ChatSearch.Run(convs, "brown");
        Assert.Single(hits);
        Assert.Equal("a", hits[0].Id);
        Assert.Contains("brown", hits[0].Snippet);
    }
}
