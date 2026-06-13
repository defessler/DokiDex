using App;

namespace App.Tests;

public class ReportFormatterTests
{
    [Theory]
    [InlineData("hello world", "=== HELLO WORLD ===")]
    [InlineData("  spaced   out  ", "=== SPACED OUT ===")]
    [InlineData("tab\tand\nnewline", "=== TAB AND NEWLINE ===")]
    public void FormatHeader_NormalizesAndUppercases(string input, string expected) =>
        Assert.Equal(expected, ReportFormatter.FormatHeader(input));

    [Theory]
    [InlineData("hello world", "--- hello world ---")]
    [InlineData("  spaced   out  ", "--- spaced out ---")]
    public void FormatFooter_NormalizesKeepsCase(string input, string expected) =>
        Assert.Equal(expected, ReportFormatter.FormatFooter(input));
}
