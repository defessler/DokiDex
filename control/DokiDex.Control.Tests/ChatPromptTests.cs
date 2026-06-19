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

    // Read role+content off a ChatTurn (RecentTurns returns turns, not the anonymous message objects above).
    private static (string role, string content) Read2(ChatTurn t) => (t.Role, t.Content);

    // Read role + the RAW content object (string OR the multimodal object[]) off whatever the builder emits.
    private static (string role, object content) ReadRaw(object msg)
    {
        var t = msg.GetType();
        var role = t.GetProperty("role")!.GetValue(msg) as string ?? "";
        var content = t.GetProperty("content")!.GetValue(msg)!;
        return (role, content);
    }

    // Read a single property by name off an anonymous object via reflection.
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

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

    [Fact]
    public void Active_lore_is_injected_after_the_card_bundle_and_before_history(  )
    {
        // ordering: system bundle -> [World Info] -> history -> user turn (P3 lorebook injection).
        var hist = new List<ChatTurn> { U("first user"), A("first reply") };
        var lore = new List<LoreEntry>
        {
            new(Keys: "dragon", Content: "Dragons rule the north.", Enabled: true),
        };
        var msgs = ChatPrompt.Build(Card(system: "You are Doki."), hist, "the new turn",
            historyTurnBudget: 20, activeLore: lore);

        Assert.Equal(5, msgs.Count); // system + [World Info] + 2 history + user

        Assert.Equal("system", Read(msgs[0]).role);
        Assert.Contains("You are Doki.", Read(msgs[0]).content);

        var (r1, c1) = Read(msgs[1]);
        Assert.Equal("system", r1);
        Assert.Contains("World Info", c1);
        Assert.Contains("Dragons rule the north.", c1);

        Assert.Equal(("user", "first user"), Read(msgs[2]));
        Assert.Equal(("assistant", "first reply"), Read(msgs[3]));
        Assert.Equal(("user", "the new turn"), Read(msgs[4]));
    }

    // ---- P5 vision-in-chat: when an imageDataUrl is supplied, the new USER turn's content becomes the OpenAI
    //      multimodal array EXACTLY like Vision.cs builds it (text + image_url), and the plain-string path is
    //      preserved EXACTLY when no image is supplied. ----

    [Fact]
    public void Image_data_url_makes_the_user_turn_a_multimodal_text_plus_image_url_array()
    {
        const string dataUrl = "data:image/png;base64,AAAA";
        var msgs = ChatPrompt.Build(Card(system: "You are Doki."), new List<ChatTurn>(), "what is this?",
            historyTurnBudget: 20, imageDataUrl: dataUrl);

        // system + the single multimodal user turn.
        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", Read(msgs[0]).role);

        var (role, content) = ReadRaw(msgs[1]);
        Assert.Equal("user", role);

        // The content is the 2-element object[]: { type="text", text=<message> }, { type="image_url", image_url={ url } }.
        var arr = Assert.IsAssignableFrom<object[]>(content);
        Assert.Equal(2, arr.Length);

        Assert.Equal("text", Prop(arr[0], "type"));
        Assert.Equal("what is this?", Prop(arr[0], "text"));

        Assert.Equal("image_url", Prop(arr[1], "type"));
        var imageUrl = Prop(arr[1], "image_url")!;
        Assert.Equal(dataUrl, Prop(imageUrl, "url"));
    }

    [Fact]
    public void Image_turn_still_follows_system_and_trimmed_history()
    {
        var hist = new List<ChatTurn> { U("first user"), A("first reply") };
        var msgs = ChatPrompt.Build(Card(), hist, "look",
            historyTurnBudget: 20, imageDataUrl: "data:image/jpeg;base64,ZZ");

        // system + 2 history turns (plain strings) + the multimodal user turn.
        Assert.Equal(4, msgs.Count);
        Assert.Equal("system", Read(msgs[0]).role);
        Assert.Equal(("user", "first user"), Read(msgs[1]));
        Assert.Equal(("assistant", "first reply"), Read(msgs[2]));

        var (role, content) = ReadRaw(msgs[3]);
        Assert.Equal("user", role);
        Assert.IsAssignableFrom<object[]>(content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_image_data_url_keeps_the_plain_string_user_content(string? dataUrl)
    {
        var msgs = ChatPrompt.Build(Card(), new List<ChatTurn>(), "hi there",
            historyTurnBudget: 20, imageDataUrl: dataUrl);

        Assert.Equal(2, msgs.Count);
        var (role, content) = ReadRaw(msgs[1]);
        Assert.Equal("user", role);
        Assert.Equal("hi there", Assert.IsType<string>(content));   // EXACT plain-string content preserved
    }

    [Fact]
    public void No_image_argument_preserves_the_exact_default_string_output()
    {
        // The default (no imageDataUrl arg) must be byte-for-byte the pre-P5 output: plain-string user content.
        var withDefault = ChatPrompt.Build(Card(), new List<ChatTurn>(), "go", historyTurnBudget: 20);
        var (role, content) = ReadRaw(withDefault[1]);
        Assert.Equal("user", role);
        Assert.Equal("go", Assert.IsType<string>(content));
    }

    // ---- RecentTurns: the shared "recent non-empty turns within budget" window (single source of truth used by
    //      BOTH Build's history trim and Chat.ActivateLore's keyword-scan window). ----

    [Fact]
    public void RecentTurns_keeps_only_non_empty_turns_in_chronological_order()
    {
        var hist = new List<ChatTurn> { U("  "), A("real reply"), U(""), U("second user") };
        var kept = ChatPrompt.RecentTurns(hist, budget: 20);

        Assert.Equal(2, kept.Count);
        Assert.Equal(("assistant", "real reply"), Read2(kept[0]));
        Assert.Equal(("user", "second user"), Read2(kept[1]));
    }

    [Fact]
    public void RecentTurns_takes_the_most_recent_budget_after_dropping_blanks()
    {
        // blank turns interleaved; only non-empty count toward the budget, most-recent-wins, chronological.
        var hist = new List<ChatTurn> { U("t1"), A(" "), U("t2"), A("t3"), U(""), A("t4") };
        var kept = ChatPrompt.RecentTurns(hist, budget: 2);

        Assert.Equal(2, kept.Count);
        Assert.Equal(("assistant", "t3"), Read2(kept[0]));
        Assert.Equal(("assistant", "t4"), Read2(kept[1]));
    }

    [Fact]
    public void RecentTurns_returns_all_non_empty_when_under_budget()
    {
        var hist = new List<ChatTurn> { U("a"), A("b") };
        var kept = ChatPrompt.RecentTurns(hist, budget: 20);
        Assert.Equal(2, kept.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecentTurns_with_a_non_positive_budget_is_empty(int budget)
    {
        var hist = new List<ChatTurn> { U("a"), A("b") };
        Assert.Empty(ChatPrompt.RecentTurns(hist, budget));
    }

    [Fact]
    public void RecentTurns_on_empty_or_all_blank_history_is_empty()
    {
        Assert.Empty(ChatPrompt.RecentTurns(new List<ChatTurn>(), budget: 20));
        Assert.Empty(ChatPrompt.RecentTurns(new List<ChatTurn> { U(" "), A("") }, budget: 20));
    }

    [Fact]
    public void Null_or_empty_active_lore_preserves_the_exact_default_output()
    {
        var hist = new List<ChatTurn> { U("first user"), A("first reply") };
        var withDefault = ChatPrompt.Build(Card(), hist, "go", historyTurnBudget: 20);
        var withNull = ChatPrompt.Build(Card(), hist, "go", historyTurnBudget: 20, activeLore: null);
        var withEmpty = ChatPrompt.Build(Card(), hist, "go", historyTurnBudget: 20, activeLore: new List<LoreEntry>());

        Assert.Equal(withDefault.Count, withNull.Count);
        Assert.Equal(withDefault.Count, withEmpty.Count);
        for (int i = 0; i < withDefault.Count; i++)
        {
            Assert.Equal(Read(withDefault[i]), Read(withNull[i]));
            Assert.Equal(Read(withDefault[i]), Read(withEmpty[i]));
        }
    }
}
