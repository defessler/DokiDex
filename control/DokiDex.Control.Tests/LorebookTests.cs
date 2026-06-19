using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Lorebook-lite / "World Info": keyword-triggered context injection. The PURE heart is Lorebook.Activate —
// whole-word, case-insensitive key match (reusing the \b{Regex.Escape}\b technique proven in Tts.ApplyLexicon),
// enabled-only, de-dup, capped to maxEntries + a cumulative maxChars budget. Total + side-effect-free (no GPU,
// no disk), so the activation rules are locked here (mirroring TtsTests/DirectorTests discipline). The disk
// store round-trip exercises the real RecipeStore.SafeName-guarded file store under RepoPaths.Root.
public class LorebookTests
{
    private static LoreEntry E(string keys, string content, bool enabled = true)
        => new(Keys: keys, Content: content, Enabled: enabled);

    [Fact]
    public void A_keyword_hit_returns_the_entry()
    {
        var entries = new List<LoreEntry> { E("dragon", "Dragons rule the north.") };
        var hit = Lorebook.Activate(entries, "Tell me about the dragon legends.", maxEntries: 8, maxChars: 1500);
        Assert.Single(hit);
        Assert.Equal("Dragons rule the north.", hit[0].Content);
    }

    [Fact]
    public void No_hit_returns_nothing()
    {
        var entries = new List<LoreEntry> { E("dragon", "Dragons rule the north.") };
        var hit = Lorebook.Activate(entries, "Tell me about the weather today.", maxEntries: 8, maxChars: 1500);
        Assert.Empty(hit);
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        var entries = new List<LoreEntry> { E("Dragon", "canon") };
        var hit = Lorebook.Activate(entries, "the DRAGON roared", maxEntries: 8, maxChars: 1500);
        Assert.Single(hit);
    }

    [Fact]
    public void A_disabled_entry_never_fires()
    {
        var entries = new List<LoreEntry> { E("dragon", "canon", enabled: false) };
        var hit = Lorebook.Activate(entries, "the dragon roared", maxEntries: 8, maxChars: 1500);
        Assert.Empty(hit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(", ,")]
    public void An_entry_with_empty_or_blank_keys_never_fires(string keys)
    {
        var entries = new List<LoreEntry> { E(keys, "canon") };
        var hit = Lorebook.Activate(entries, "anything at all here", maxEntries: 8, maxChars: 1500);
        Assert.Empty(hit);
    }

    [Fact]
    public void Any_of_several_comma_separated_keys_fires_the_entry()
    {
        var entries = new List<LoreEntry> { E("wyvern, dragon, drake", "canon") };
        var hit = Lorebook.Activate(entries, "I saw a drake.", maxEntries: 8, maxChars: 1500);
        Assert.Single(hit);
    }

    [Fact]
    public void A_partial_word_does_not_match()
    {
        // key 'cat' must NOT fire on 'category'.
        var entries = new List<LoreEntry> { E("cat", "feline canon") };
        var hit = Lorebook.Activate(entries, "pick a category for this", maxEntries: 8, maxChars: 1500);
        Assert.Empty(hit);
    }

    [Fact]
    public void MaxEntries_caps_the_count()
    {
        var entries = new List<LoreEntry>
        {
            E("alpha", "A"), E("bravo", "B"), E("charlie", "C"), E("delta", "D"),
        };
        var hit = Lorebook.Activate(entries, "alpha bravo charlie delta", maxEntries: 2, maxChars: 1500);
        Assert.Equal(2, hit.Count);
    }

    [Fact]
    public void MaxChars_truncates_on_a_cumulative_budget()
    {
        var entries = new List<LoreEntry>
        {
            E("alpha", new string('a', 100)),
            E("bravo", new string('b', 100)),
            E("charlie", new string('c', 100)),
        };
        // budget 250: first two (100+100=200) fit; the third (would be 300) does not.
        var hit = Lorebook.Activate(entries, "alpha bravo charlie", maxEntries: 8, maxChars: 250);
        Assert.Equal(2, hit.Count);
    }

    [Fact]
    public void Duplicate_entries_are_de_duplicated()
    {
        var dup = E("dragon", "same canon");
        var entries = new List<LoreEntry> { dup, dup };
        var hit = Lorebook.Activate(entries, "the dragon roared", maxEntries: 8, maxChars: 1500);
        Assert.Single(hit);
    }

    [Fact]
    public void A_non_matching_entry_does_not_suppress_a_later_matching_entry_with_the_same_content()
    {
        // The non-matching entry (key 'griffin', absent from scanText) shares Keys+Content with a LATER entry
        // whose OTHER key ('dragon') DOES appear. De-dup must run only on ACTIVATED entries, so the first
        // (gated out by the keyword-match) must NOT consume the de-dup slot and suppress the second.
        var entries = new List<LoreEntry>
        {
            E("griffin", "shared canon"),   // does NOT match scanText
            E("dragon",  "shared canon"),   // DOES match scanText (same Content as above)
        };
        var hit = Lorebook.Activate(entries, "the dragon roared", maxEntries: 8, maxChars: 1500);
        Assert.Single(hit);
        Assert.Equal("dragon", hit[0].Keys);
        Assert.Equal("shared canon", hit[0].Content);
    }

    // ---- disk store (RecipeStore.SafeName-guarded, clone of Persona/RecipeStore) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("bad/slash")]
    public void Save_rejects_an_unsafe_or_empty_name(string? name)
        => Assert.False(Lorebook.Save(new LoreBook(name, new List<LoreEntry> { E("k", "v") })));

    [Fact]
    public void Delete_rejects_an_unsafe_name_without_touching_disk()
        => Assert.False(Lorebook.Delete("../../etc"));

    [Fact]
    public void Save_then_load_round_trips_entries()
    {
        var name = "doki test lore zz";
        try
        {
            var book = new LoreBook(name, new List<LoreEntry>
            {
                E("dragon, wyrm", "Dragons rule the north."),
                E("castle", "The keep is ancient.", enabled: false),
            });
            Assert.True(Lorebook.Save(book));

            var loaded = Lorebook.Load(name);
            Assert.NotNull(loaded);
            Assert.Equal(name, loaded!.Name);
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Equal("dragon, wyrm", loaded.Entries[0].Keys);
            Assert.Equal("Dragons rule the north.", loaded.Entries[0].Content);
            Assert.False(loaded.Entries[1].Enabled);

            Assert.Contains(Lorebook.List(), n => n == name);
        }
        finally { Lorebook.Delete(name); }
    }

    [Fact]
    public void Delete_removes_the_lorebook()
    {
        var name = "doki delete lore zz";
        Lorebook.Save(new LoreBook(name, new List<LoreEntry> { E("k", "v") }));
        Assert.True(Lorebook.Delete(name));
        Assert.Null(Lorebook.Load(name));
    }
}
