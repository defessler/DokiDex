using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Saved-search name validation (reuses RecipeStore.SafeName) — the IO-free guard before any disk write.
public class SavedSearchesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("bad/slash")]
    public void Save_rejects_an_unsafe_or_empty_name(string? name)
        => Assert.False(SavedSearches.Save(new SavedSearch(name, "q", "image", "active")));

    [Fact]
    public void Delete_rejects_an_unsafe_name_without_touching_disk()
        => Assert.False(SavedSearches.Delete("../../etc"));
}
