using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure seams of the web_search sidecar (ddgs CLI -> JSON file). The live fetch needs uvx + network, so it
// degrades when absent; the argv builder + the JSON parser (the fragile bits) are locked here with no process.
public class WebSearchTests
{
    [Fact]
    public void BuildArgs_is_pure_ddgs_text_argv_with_explicit_output_file()
    {
        // EMPIRICAL contract: `-o <file>` MUST be an explicit path (a bare `-o json` writes an auto-named file
        // in CWD and prints nothing). The query + count + out path are passed as discrete argv (no shell concat).
        var a = WebSearch.BuildArgs("neon dragon", 5, @"C:\t\out.json");
        Assert.Equal("ddgs", a[0]);
        Assert.Equal("text", a[1]);
        var qi = a.ToList().IndexOf("-q"); Assert.True(qi >= 0); Assert.Equal("neon dragon", a[qi + 1]);
        var mi = a.ToList().IndexOf("-m"); Assert.True(mi >= 0); Assert.Equal("5", a[mi + 1]);
        var oi = a.ToList().IndexOf("-o"); Assert.True(oi >= 0); Assert.Equal(@"C:\t\out.json", a[oi + 1]);
    }

    [Fact]
    public void BuildArgs_clamps_a_silly_count_into_a_sane_range()
    {
        Assert.Equal("1", ResultCount(WebSearch.BuildArgs("q", 0, "o.json")));     // floor
        Assert.Equal("10", ResultCount(WebSearch.BuildArgs("q", 9999, "o.json"))); // cap
    }

    private static string ResultCount(System.Collections.Generic.IReadOnlyList<string> a)
        => a[a.ToList().IndexOf("-m") + 1];

    [Fact]
    public void ParseDdgsJson_maps_href_to_url_and_body_to_snippet()
    {
        // VERIFIED shape: a top-level array of { title, href, body } (NOT title/url/snippet).
        const string json = """
        [
          {"title":"First Result","href":"https://a.example/x","body":"snippet one"},
          {"title":"Second","href":"https://b.example/y","body":"snippet two"}
        ]
        """;
        var rows = WebSearch.ParseDdgsJson(json);
        Assert.Equal(2, rows.Count);
        Assert.Equal("First Result", rows[0].title);
        Assert.Equal("https://a.example/x", rows[0].url);   // href -> url
        Assert.Equal("snippet one", rows[0].snippet);       // body -> snippet
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]          // an object, not the expected array
    [InlineData("[]")]          // empty array
    public void ParseDdgsJson_returns_empty_on_missing_or_malformed_input(string json)
        => Assert.Empty(WebSearch.ParseDdgsJson(json));

    [Fact]
    public void FormatWebResults_bounds_a_long_snippet_so_the_tool_text_stays_small()
    {
        var longBody = new string('x', 5000);
        var text = WebSearch.FormatWebResults("dragons", new[] { ("A Title", "https://e.example/p", longBody) });
        Assert.DoesNotContain(longBody, text);                 // not re-sent verbatim across hops
        Assert.True(text.Length < 600, $"web tool result should stay small, was {text.Length} chars");
        Assert.Contains("A Title", text);
        Assert.Contains("https://e.example/p", text);
        Assert.Contains("…", text);                            // truncation marker present
    }

    [Fact]
    public void FormatWebResults_renders_a_clean_no_results_line_for_an_empty_set()
    {
        var text = WebSearch.FormatWebResults("nothing here", System.Array.Empty<(string, string, string)>());
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("no web results", text, System.StringComparison.OrdinalIgnoreCase);
    }
}
