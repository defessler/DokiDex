using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure seams of the knowledge-base sidecar (doc_index.py doc_search / doc_ingest -> stdout JSON). The live
// calls need the :8090 embed server + a built doc_index.db, so they degrade when absent; the argv builders + the
// JSON parser are locked here (no process), mirroring CodeSearchTests.
public class DocSearchTests
{
    [Fact]
    public void BuildSearchArgs_is_pure_script_doc_search_kb_query_k_argv()
    {
        var a = DocSearch.BuildSearchArgs("conv-123", "how do reactors work", 5);
        Assert.EndsWith("doc_index.py", a[0]);
        Assert.Equal("doc_search", a[1]);
        Assert.Equal("conv-123", a[2]);
        Assert.Equal("how do reactors work", a[3]);
        Assert.Equal("5", a[4]);
    }

    [Fact]
    public void BuildSearchArgs_clamps_a_silly_k_into_a_sane_range()
    {
        Assert.Equal("1", DocSearch.BuildSearchArgs("kb", "q", 0)[4]);     // floor
        Assert.Equal("10", DocSearch.BuildSearchArgs("kb", "q", 9999)[4]); // cap
    }

    [Fact]
    public void BuildIngestArgs_is_pure_script_doc_ingest_kb_source_argv()
    {
        var a = DocSearch.BuildIngestArgs("conv-9", "manual.txt");
        Assert.EndsWith("doc_index.py", a[0]);
        Assert.Equal("doc_ingest", a[1]);
        Assert.Equal("conv-9", a[2]);
        Assert.Equal("manual.txt", a[3]);
    }

    [Fact]
    public void BuildSourcesArgs_and_BuildRemoveArgs_are_pure_argv()
    {
        var s = DocSearch.BuildSourcesArgs("conv-1");
        Assert.Equal("doc_sources", s[1]);
        Assert.Equal("conv-1", s[2]);

        var r = DocSearch.BuildRemoveArgs("conv-1", "doc.txt");
        Assert.Equal("doc_remove", r[1]);
        Assert.Equal("conv-1", r[2]);
        Assert.Equal("doc.txt", r[3]);
    }

    [Fact]
    public void BuildDeleteArgs_is_pure_script_doc_delete_kb_argv()
    {
        var d = DocSearch.BuildDeleteArgs("conv-1");
        Assert.EndsWith("doc_index.py", d[0]);
        Assert.Equal("doc_delete", d[1]);
        Assert.Equal("conv-1", d[2]);
    }

    // ---- ingest size cap (FIX 1): a doc whose text exceeds MaxDocChars is rejected with a CLEAR message BEFORE
    //      the long embed loop (never the misleading 30s-timeout "start the embed server" 503). The validator is
    //      pure, so the bound is locked here. ----

    [Fact]
    public void ValidateIngest_accepts_text_at_or_under_the_cap()
    {
        // An at-cap doc (exactly MaxDocChars) is accepted — null message, no rejection.
        Assert.Null(DocSearch.ValidateIngest(new string('a', DocSearch.MaxDocChars)));
        Assert.Null(DocSearch.ValidateIngest("a short doc"));
    }

    [Fact]
    public void ValidateIngest_rejects_text_over_the_cap_with_a_clear_message()
    {
        var over = new string('a', DocSearch.MaxDocChars + 1);
        var msg = DocSearch.ValidateIngest(over);
        Assert.NotNull(msg);
        Assert.Contains("too large", msg!);                              // a clear "document too large …" message
        Assert.Contains((DocSearch.MaxDocChars + 1).ToString(), msg!);   // names the offending size
        Assert.Contains("smaller", msg!);                               // tells the user how to proceed (split / smaller file)
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateIngest_treats_null_or_empty_text_as_acceptable(string? text)
    {
        // Empty/blank text is the endpoint's own concern ("empty document text"); the size validator only guards
        // the UPPER bound, so it must not reject empty input (no double-rejection / spurious message).
        Assert.Null(DocSearch.ValidateIngest(text));
    }

    [Fact]
    public async Task IngestAsync_rejects_an_over_cap_doc_with_the_clear_message_not_a_503()
    {
        // The over-cap doc fails FAST with the clear validator message — never the "start the embed server" 503 —
        // and the size gate runs BEFORE the install/spawn checks, so it holds even without python/uv present.
        var over = new string('a', DocSearch.MaxDocChars + 1);
        var r = await DocSearch.IngestAsync("conv-1", "huge.txt", over, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(0, r.Chunks);
        Assert.NotNull(r.Message);
        Assert.Contains("too large", r.Message!);
        Assert.DoesNotContain("embed server", r.Message!);   // NOT the misleading timeout-503 message
    }

    [Fact]
    public void ParseDocJson_reads_source_ord_score_and_content()
    {
        // VERIFIED shape: a JSON array of { source, ord, content, score }.
        const string json = """
        [
          {"source":"lore.txt","ord":0,"content":"Dragons rule the north.","score":0.91},
          {"source":"geo.txt","ord":2,"content":"The ocean lies south.","score":0.74}
        ]
        """;
        var rows = DocSearch.ParseDocJson(json);
        Assert.Equal(2, rows.Count);
        Assert.Equal("lore.txt", rows[0].source);
        Assert.Equal(0, rows[0].ord);
        Assert.Equal(0.91, rows[0].score, 3);
        Assert.Contains("Dragons rule", rows[0].content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ParseDocJson_returns_empty_on_missing_or_malformed_input(string json)
        => Assert.Empty(DocSearch.ParseDocJson(json));

    [Fact]
    public void ParseDocJson_maps_rows_to_DocChunks_for_injection()
    {
        const string json = """[{"source":"a.txt","ord":1,"content":"fact","score":0.8}]""";
        var docs = DocSearch.ToDocChunks(DocSearch.ParseDocJson(json));
        var d = Assert.Single(docs);
        Assert.Equal("a.txt", d.Source);
        Assert.Equal("fact", d.Content);
        Assert.Equal(0.8, d.Score, 3);
    }

    [Fact]
    public void ToDocChunks_on_empty_rows_is_an_empty_list_not_null()
    {
        var docs = DocSearch.ToDocChunks(System.Array.Empty<(string, int, string, double)>());
        Assert.NotNull(docs);
        Assert.Empty(docs);
    }
}
