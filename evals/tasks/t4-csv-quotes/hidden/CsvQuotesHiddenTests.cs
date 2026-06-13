using App;

namespace App.Tests;

public class CsvQuotesHiddenTests
{
    [Fact]
    public void QuotedFieldWithComma() =>
        Assert.Equal(new[] { "a", "b,c", "d" }, CsvParser.ParseLine("a,\"b,c\",d"));

    [Fact]
    public void EscapedQuoteInsideQuotedField() =>
        Assert.Equal(new[] { "x\"y", "z" }, CsvParser.ParseLine("\"x\"\"y\",z"));

    [Fact]
    public void PlainFieldsStillWork() =>
        Assert.Equal(new[] { "a", "b", "c" }, CsvParser.ParseLine("a,b,c"));

    [Fact]
    public void QuotedEmptyField() =>
        Assert.Equal(new[] { "", "b" }, CsvParser.ParseLine("\"\",b"));
}
