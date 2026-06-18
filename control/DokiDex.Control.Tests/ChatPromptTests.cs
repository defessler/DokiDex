using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure heart of persona chat: assembling a persona card + trimmed multi-turn history + the new user turn
// into the OpenAI message[] that reaches llama-swap. Total + side-effect-free (no GPU, no disk), so the
// fragile ordering/trim rules are locked here — mirroring DirectorTests/VisionTests discipline.
public class ChatPromptTests
{
    // Read role+content off whatever anonymous/object the builder emits, so the test asserts shape not type.
    private static (string role, string content) Read(object msg)
    {
        var t = msg.GetType();
        var role = t.GetProperty("role")!.GetValue(msg) as string ?? "";
        var content = t.GetProperty("content")!.GetValue(msg) as string ?? "";
        return (role, content);
    }

    private static PersonaCard Card(string system = "You are Doki.", string persona = "", string greeting = "", string examples = "")
        => new(Name: "Doki", Avatar: null, System: system, Persona: persona, Greeting: greeting,
               Examples: examples, Tier: null, Voice: null, Lorebook: null);

    private static ChatTurn U(string c) => new("user", c, null);
    private static ChatTurn A(string c) => new("assistant", c, null);

    [Fact]
    public void Card_only_yields_one_system_turn_then_the_user_turn()
    {
        var msgs = ChatPrompt.Build(Card(system: "You are Doki."), new List<ChatTurn>(), "hi there", historyTurnBudget: 20);

        Assert.Equal(2, msgs.Count);
        var (r0, c0) = Read(msgs[0]);
        Assert.Equal("system", r0);
        Assert.Contains("You are Doki.", c0);

        var (r1, c1) = Read(msgs[1]);
        Assert.Equal("user", r1);
        Assert.Equal("hi there", c1);
    }

    [Fact]
    public void System_bundle_folds_in_persona_and_examples()
    {
        var card = Card(system: "BEHAVIOR", persona: "USER-IDENTITY", examples: "EXAMPLE-DIALOGUE");
        var msgs = ChatPrompt.Build(card, new List<ChatTurn>(), "go", historyTurnBudget: 20);

        var (_, sys) = Read(msgs[0]);
        Assert.Contains("BEHAVIOR", sys);
        Assert.Contains("USER-IDENTITY", sys);
        Assert.Contains("EXAMPLE-DIALOGUE", sys);
    }

    [Fact]
    public void Card_then_history_then_user_in_order()
    {
        var hist = new List<ChatTurn> { U("first user"), A("first reply"), U("second user"), A("second reply") };
        var msgs = ChatPrompt.Build(Card(), hist, "the new turn", historyTurnBudget: 20);

        // system, then the 4 history turns in chronological order, then the new user turn.
        Assert.Equal(6, msgs.Count);
        Assert.Equal("system", Read(msgs[0]).role);
        Assert.Equal(("user", "first user"), Read(msgs[1]));
        Assert.Equal(("assistant", "first reply"), Read(msgs[2]));
        Assert.Equal(("user", "second user"), Read(msgs[3]));
        Assert.Equal(("assistant", "second reply"), Read(msgs[4]));
        Assert.Equal(("user", "the new turn"), Read(msgs[5]));
    }

    [Fact]
    public void History_is_trimmed_to_budget_most_recent_wins()
    {
        // 5 history turns, budget = 2 => only the LAST two survive (chronological), plus system + new user.
        var hist = new List<ChatTurn> { U("t1"), A("t2"), U("t3"), A("t4"), U("t5") };
        var msgs = ChatPrompt.Build(Card(), hist, "now", historyTurnBudget: 2);

        Assert.Equal(4, msgs.Count); // system + 2 kept history + new user
        Assert.Equal("system", Read(msgs[0]).role);
        Assert.Equal(("assistant", "t4"), Read(msgs[1]));
        Assert.Equal(("user", "t5"), Read(msgs[2]));
        Assert.Equal(("user", "now"), Read(msgs[3]));
    }

    [Fact]
    public void Zero_or_negative_budget_drops_all_history()
    {
        var hist = new List<ChatTurn> { U("a"), A("b") };
        var msgs = ChatPrompt.Build(Card(), hist, "q", historyTurnBudget: 0);

        Assert.Equal(2, msgs.Count); // system + new user only
        Assert.Equal("system", Read(msgs[0]).role);
        Assert.Equal(("user", "q"), Read(msgs[1]));
    }

    [Fact]
    public void Null_or_default_card_still_produces_a_usable_system_turn()
    {
        // The default uncensored studio-aware persona ships in code, so raw chat works with zero setup.
        var msgs = ChatPrompt.Build(null, new List<ChatTurn>(), "raw chat", historyTurnBudget: 20);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", Read(msgs[0]).role);
        Assert.False(string.IsNullOrWhiteSpace(Read(msgs[0]).content)); // a real built-in system prompt
        Assert.Equal(("user", "raw chat"), Read(msgs[1]));
    }

    [Fact]
    public void Empty_history_entries_are_skipped()
    {
        var hist = new List<ChatTurn> { U("  "), A("real reply"), U("") };
        var msgs = ChatPrompt.Build(Card(), hist, "go", historyTurnBudget: 20);

        // only the non-empty assistant turn survives between system and the new user turn.
        Assert.Equal(3, msgs.Count);
        Assert.Equal(("assistant", "real reply"), Read(msgs[1]));
        Assert.Equal(("user", "go"), Read(msgs[2]));
    }
}
