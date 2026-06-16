using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The CSV batch parser — the part where bugs live (quoting, embedded commas/newlines, header mapping). Pure,
// locked here with no GPU.
public class CsvTests
{
    [Fact]
    public void Simple_rows_split_on_commas_and_newlines()
    {
        var rows = Csv.Parse("a,b\n1,2\n3,4");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "1", "2" }, rows[1]);
    }

    [Fact]
    public void Quoted_field_keeps_its_comma()
    {
        var rows = Csv.Parse("prompt,kind\n\"a fox, in the rain\",image");
        Assert.Equal("a fox, in the rain", rows[1][0]);
        Assert.Equal("image", rows[1][1]);
    }

    [Fact]
    public void Escaped_double_quote_becomes_one_quote()
        => Assert.Equal("a \"neon\" sign", Csv.Parse("\"a \"\"neon\"\" sign\"")[0][0]);

    [Fact]
    public void Newline_inside_quotes_does_not_break_the_row()
    {
        var rows = Csv.Parse("prompt\n\"line one\nline two\"");
        Assert.Equal(2, rows.Count);              // header + one data row, NOT three
        Assert.Equal("line one\nline two", rows[1][0]);
    }

    [Fact]
    public void Header_mapping_is_case_insensitive_and_skips_blank_lines()
    {
        var rows = Csv.ParseWithHeader("Prompt,Seed\na fox,7\n\nb cat,9\n");
        Assert.Equal(2, rows.Count);              // blank line skipped
        Assert.Equal("a fox", rows[0]["prompt"]); // case-insensitive key
        Assert.Equal("7", rows[0]["SEED"]);
    }

    [Fact]
    public void Short_rows_leave_missing_columns_absent()
    {
        var rows = Csv.ParseWithHeader("prompt,kind,seed\nonly a prompt");
        Assert.Single(rows);
        Assert.Equal("only a prompt", rows[0]["prompt"]);
        Assert.False(rows[0].ContainsKey("kind"));
    }

    [Fact]
    public void Header_only_or_empty_yields_no_rows()
    {
        Assert.Empty(Csv.ParseWithHeader("prompt,kind"));
        Assert.Empty(Csv.ParseWithHeader(""));
        Assert.Empty(Csv.ParseWithHeader(null));
    }
}
