using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The security-critical pure core of saved recipes: turning a user-supplied name into a safe file stem
// (no traversal, no separators, bounded charset). Pure, no filesystem, no GPU.
public class RecipeStoreTests
{
    [Theory]
    [InlineData("my recipe", "my recipe")]
    [InlineData("  trimmed  ", "trimmed")]
    [InlineData("portrait-v2_final", "portrait-v2_final")]
    public void Valid_names_pass_through(string input, string expected)
        => Assert.Equal(expected, RecipeStore.SafeName(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("../secrets")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("name.with.dots")]   // '.' not in the allowed set
    [InlineData("bad*name")]
    [InlineData("semi;colon")]
    public void Unsafe_or_empty_names_are_rejected(string? input)
        => Assert.Null(RecipeStore.SafeName(input));

    [Fact]
    public void Overly_long_names_are_rejected()
        => Assert.Null(RecipeStore.SafeName(new string('a', 65)));
}
