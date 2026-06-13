using App;

namespace App.Tests;

public class ReportFormatterHiddenTests
{
    [Fact]
    public void Header_Null_IsEmptyBanner() =>
        Assert.Equal("===  ===", ReportFormatter.FormatHeader(null));

    [Fact]
    public void Footer_ControlChars_Stripped() =>
        Assert.Equal("--- ab ---", ReportFormatter.FormatFooter("ab"));

    [Fact]
    public void Header_MixedWhitespace_SingleSpaced() =>
        Assert.Equal("=== A B C ===", ReportFormatter.FormatHeader("a \t b\r\nc"));
}
