using App;

namespace App.Tests;

public class StringUtilsTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("hello", 1)]
    [InlineData("hello world", 2)]
    [InlineData("  many   spaced   words  ", 3)]
    public void WordCount_CountsWords(string? input, int expected) =>
        Assert.Equal(expected, StringUtils.WordCount(input));

    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("  Foo!! Bar??  ", "foo-bar")]
    [InlineData("Multiple   Spaces", "multiple-spaces")]
    [InlineData("C# Is Great", "c-is-great")]
    public void Slugify_NormalizesText(string input, string expected) =>
        Assert.Equal(expected, StringUtils.Slugify(input));
}
