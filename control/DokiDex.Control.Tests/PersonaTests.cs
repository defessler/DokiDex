using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Persona card store: a file-based clone of References/SavedSearches, guarded by RecipeStore.SafeName.
// Save-rejection (the security-critical guard) is pure-ish; the round-trip exercises the real disk store
// under RepoPaths.Root (it creates + cleans its own personas/ entry).
public class PersonaTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("bad/slash")]
    public void Save_rejects_an_unsafe_or_empty_name(string? name)
        => Assert.False(Persona.Save(new PersonaCard(name, null, "sys", "", "", "", null, null, null)));

    [Fact]
    public void Delete_rejects_an_unsafe_name_without_touching_disk()
        => Assert.False(Persona.Delete("../../etc"));

    [Fact]
    public void Save_then_load_round_trips_all_fields()
    {
        var name = "doki test persona zz";
        try
        {
            var card = new PersonaCard(name, "av.png", "BEHAVIOR", "USER", "hello!", "EXAMPLES", "fast", "narrator", "lore1");
            Assert.True(Persona.Save(card));

            var loaded = Persona.Load(name);
            Assert.NotNull(loaded);
            Assert.Equal(name, loaded!.Name);
            Assert.Equal("BEHAVIOR", loaded.System);
            Assert.Equal("USER", loaded.Persona);
            Assert.Equal("hello!", loaded.Greeting);
            Assert.Equal("EXAMPLES", loaded.Examples);
            Assert.Equal("fast", loaded.Tier);
            Assert.Equal("narrator", loaded.Voice);
            Assert.Equal("lore1", loaded.Lorebook);

            Assert.Contains(Persona.List(), p => p.Name == name);
        }
        finally { Persona.Delete(name); }
    }

    [Fact]
    public void Delete_removes_the_card()
    {
        var name = "doki delete me zz";
        Persona.Save(new PersonaCard(name, null, "x", "", "", "", null, null, null));
        Assert.True(Persona.Delete(name));
        Assert.Null(Persona.Load(name));
    }
}
