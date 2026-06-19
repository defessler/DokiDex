using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure seams of the code_search sidecar (code_index.py search -> stdout JSON). The live search needs the
// :8090 embed server + a built index, so it degrades when absent; the argv builder + JSON parser are locked here.
public class CodeSearchTests
{
    [Fact]
    public void BuildArgs_is_pure_script_search_query_k_argv()
    {
        var a = CodeSearch.BuildArgs("where is the agent loop", 5);
        Assert.EndsWith("code_index.py", a[0]);
        Assert.Equal("search", a[1]);
        Assert.Equal("where is the agent loop", a[2]);
        Assert.Equal("5", a[3]);
    }

    [Fact]
    public void BuildArgs_clamps_a_silly_k_into_a_sane_range()
    {
        Assert.Equal("1", CodeSearch.BuildArgs("q", 0)[3]);     // floor
        Assert.Equal("10", CodeSearch.BuildArgs("q", 9999)[3]); // cap
    }

    [Fact]
    public void ParseCodeJson_reads_path_linerange_score_and_content()
    {
        // VERIFIED shape: a JSON array of { path, start_line, end_line, content, score }.
        const string json = """
        [
          {"path":"control/Web/Chat.cs","start_line":190,"end_line":222,"content":"for (var hop...","score":0.83},
          {"path":"control/Web/ChatTools.cs","start_line":85,"end_line":94,"content":"switch...","score":0.71}
        ]
        """;
        var rows = CodeSearch.ParseCodeJson(json);
        Assert.Equal(2, rows.Count);
        Assert.Equal("control/Web/Chat.cs", rows[0].path);
        Assert.Equal(190, rows[0].start);
        Assert.Equal(222, rows[0].end);
        Assert.Equal(0.83, rows[0].score, 3);
        Assert.Contains("for (var hop", rows[0].content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ParseCodeJson_returns_empty_on_missing_or_malformed_input(string json)
        => Assert.Empty(CodeSearch.ParseCodeJson(json));

    [Fact]
    public void FormatCodeResults_bounds_a_long_chunk_so_the_tool_text_stays_small()
    {
        var longChunk = new string('x', 5000);
        var text = CodeSearch.FormatCodeResults("loop",
            new[] { ("control/Web/Chat.cs", 1, 9, longChunk, 0.9) });
        Assert.DoesNotContain(longChunk, text);                // not re-sent verbatim across hops
        Assert.True(text.Length < 700, $"code tool result should stay small, was {text.Length} chars");
        Assert.Contains("control/Web/Chat.cs", text);
        Assert.Contains("1-9", text);                          // the line range survives
        Assert.Contains("…", text);                            // truncation marker present
    }

    [Fact]
    public void FormatCodeResults_renders_a_clean_no_results_line_for_an_empty_set()
    {
        var text = CodeSearch.FormatCodeResults("nothing",
            System.Array.Empty<(string, int, int, string, double)>());
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("no matching code", text, System.StringComparison.OrdinalIgnoreCase);
    }
}
