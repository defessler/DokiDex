using App;

namespace App.Tests;

public class WordCountWhitespaceHiddenTests
{
    [Theory]
    [InlineData("a\tb\tc", 3)]
    [InlineData("a\nb\nc", 3)]
    [InlineData("a\r\nb", 2)]
    [InlineData("mixed \t spacing\nhere", 3)]
    public void WordCount_HandlesAllWhitespace(string input, int expected) =>
        Assert.Equal(expected, StringUtils.WordCount(input));
}
