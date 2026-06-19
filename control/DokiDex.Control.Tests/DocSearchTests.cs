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

    // ---- binary ingest (PDF/docx): a SEPARATE subcommand `doc_ingest_bin` whose argv carries the same KB +
    //      SOURCE, but whose `uv run` invocation adds the `--with pypdf --with python-docx` overlay (asserted via
    //      BuildIngestBinUvArgs) so the parser deps are present ONLY on this path. The txt/md doc_ingest argv is
    //      left untouched (zero-dep). Both builders are pure, so they're locked here with no process. ----
    [Fact]
    public void BuildIngestBinArgs_is_pure_script_doc_ingest_bin_kb_source_argv()
    {
        var a = DocSearch.BuildIngestBinArgs("conv-9", "manual.pdf");
        Assert.EndsWith("doc_index.py", a[0]);
        Assert.Equal("doc_ingest_bin", a[1]);
        Assert.Equal("conv-9", a[2]);
        Assert.Equal("manual.pdf", a[3]);
    }

    [Fact]
    public void BuildIngestBinUvArgs_prepends_run_python_and_the_parser_with_overlay()
    {
        // The binary path is the ONLY one that adds `--with pypdf --with python-docx` to `uv run`; the overlay
        // must come BEFORE `python` (uv consumes --with on the `run` verb), and the script subcommand follows.
        var uv = DocSearch.BuildIngestBinUvArgs("conv-9", "manual.pdf");
        Assert.Equal("run", uv[0]);
        Assert.Contains("--with", uv);
        Assert.Contains("pypdf", uv);
        Assert.Contains("python-docx", uv);
        var py = uv.ToList().IndexOf("python");
        var withIdx = uv.ToList().IndexOf("--with");
        Assert.True(withIdx >= 0 && withIdx < py, "--with overlay precedes the `python` token");
        Assert.Equal("doc_ingest_bin", uv[py + 2]);   // python <script> doc_ingest_bin ...
    }

    [Fact]
    public void BuildIngestUvArgs_for_the_text_path_stays_zero_dep_no_with_overlay()
    {
        // INVARIANT: the txt/md / paste path must NEVER carry a --with overlay (it stays pure-stdlib, no resolve).
        var uv = DocSearch.BuildIngestUvArgs("conv-9", "notes.txt");
        Assert.Equal("run", uv[0]);
        Assert.DoesNotContain("--with", uv);
        Assert.DoesNotContain("pypdf", uv);
        var py = uv.ToList().IndexOf("python");
        Assert.Equal("doc_ingest", uv[py + 2]);
    }

    [Fact]
    public async Task IngestBinAsync_rejects_an_over_cap_byte_payload_with_the_clear_message()
    {
        // The raw-bytes ceiling (MaxDocBytes) is the PRIMARY binary gate (a PDF's bytes exceed its text). An
        // over-cap byte payload fails FAST with a clear "too large" message BEFORE any spawn — never the timeout
        // 503 — and the gate runs before the install/spawn checks so it holds without uv/python present.
        var over = new byte[DocSearch.MaxDocBytes + 1];
        var r = await DocSearch.IngestBinAsync("conv-1", "huge.pdf", over, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(0, r.Chunks);
        Assert.NotNull(r.Message);
        Assert.Contains("too large", r.Message!);
        Assert.DoesNotContain("embed server", r.Message!);
    }

    [Fact]
    public void ValidateIngestBytes_gates_on_the_raw_byte_ceiling()
    {
        Assert.Null(DocSearch.ValidateIngestBytes(new byte[DocSearch.MaxDocBytes]));   // at-cap accepted
        var msg = DocSearch.ValidateIngestBytes(new byte[DocSearch.MaxDocBytes + 1]);
        Assert.NotNull(msg);
        Assert.Contains("too large", msg!);
    }

    [Fact]
    public void MaxUploadBytes_bounds_the_chunked_multipart_body_just_above_MaxDocBytes()
    {
        // FIX 6: the /docs/file endpoint caps ReadFormAsync's MultipartBodyLengthLimit at MaxUploadBytes so a
        // CHUNKED upload (no Content-Length, so the cheap 413 pre-check can't fire) can't force the whole body to
        // buffer up to Kestrel's 30MB default. The cap must be (a) at least MaxDocBytes + a real framing margin so
        // an honest at-cap file still uploads, and (b) far below Kestrel's 30MB default so the guard actually bites.
        Assert.True(DocSearch.MaxUploadBytes > DocSearch.MaxDocBytes, "leaves headroom over the payload for multipart framing");
        Assert.True(DocSearch.MaxUploadBytes - DocSearch.MaxDocBytes >= 16 * 1024, "the framing margin is a real (>=16KB) allowance");
        Assert.True(DocSearch.MaxUploadBytes < 30L * 1024 * 1024, "stays well under Kestrel's 30MB default so the cap actually bites");
    }

    // ---- binary-ingest exit-code -> message mapping (FIX 1 + FIX 5): a PURE seam so each exit code surfaces the
    //      RIGHT user-facing message without spawning uv/python. doc_ingest_bin's exits: 0 = ok (chunks may be 0
    //      for a scanned PDF), 3 = parsers couldn't load, 4 = corrupt/encrypted/wrong-format file, 5 = extracted
    //      text over MaxDocChars, any OTHER non-zero = generic (parsers unavailable offline OR embed server down). ----

    [Fact]
    public void MapIngestBinExit_exit0_is_ok_with_the_parsed_chunk_count()
    {
        var r = DocSearch.MapIngestBinExit(0, "{\"chunks\":7}");
        Assert.True(r.Ok);
        Assert.Equal(7, r.Chunks);
    }

    [Fact]
    public void MapIngestBinExit_exit0_with_zero_chunks_is_still_ok_a_scanned_pdf()
    {
        // A scanned/image-only PDF extracts to "" -> 0 chunks but exit 0: that's a benign no-op the endpoint
        // surfaces as "looks scanned", NOT a failure. Ok stays true so it isn't turned into a 503.
        var r = DocSearch.MapIngestBinExit(0, "{\"chunks\":0}");
        Assert.True(r.Ok);
        Assert.Equal(0, r.Chunks);
    }

    [Fact]
    public void MapIngestBinExit_exit3_surfaces_the_scripts_clear_parser_install_message()
    {
        // exit 3 = the parsers couldn't load: surface the script's {"error":…} (the real `uv run --with …` hint),
        // NOT the embed-down 503. The message must NOT mention the (non-existent) `setup.ps1 -Docs` switch.
        const string stdout = "{\"error\":\"the PDF/DOCX parsers couldn't load (offline? run: uv run --with pypdf --with python-docx ...)\"}";
        var r = DocSearch.MapIngestBinExit(3, stdout);
        Assert.False(r.Ok);
        Assert.Equal(0, r.Chunks);
        Assert.Contains("uv run --with pypdf --with python-docx", r.Message!);
        Assert.DoesNotContain("setup.ps1 -Docs", r.Message!);
        Assert.DoesNotContain("embed server", r.Message!);
    }

    [Fact]
    public void MapIngestBinExit_exit4_is_the_clear_corrupt_file_message_not_a_503()
    {
        // FIX 1: a corrupt/encrypted/not-really-that-format file (parser present, file unreadable) exits 4. It must
        // surface the script's clear "couldn't read this file" message — NEVER the misleading "start the embed
        // server" 503 (the embed server is fine). Falls back to a clear default if stdout carried no {"error":…}.
        const string stdout = "{\"error\":\"couldn't read this file (corrupt, encrypted, or not a valid PDF/DOCX)\"}";
        var r = DocSearch.MapIngestBinExit(4, stdout);
        Assert.False(r.Ok);
        Assert.Equal(0, r.Chunks);
        Assert.Contains("couldn't read", r.Message!);
        Assert.DoesNotContain("embed server", r.Message!);

        // even with NO parseable stdout, exit 4 must NOT degrade to the embed-server message — a clear corrupt-file
        // default stands in (the distinct exit code alone tells us it's a read failure, not a down embed server).
        var rNoBody = DocSearch.MapIngestBinExit(4, "");
        Assert.False(rNoBody.Ok);
        Assert.DoesNotContain("embed server", rNoBody.Message!);
        Assert.Contains("read", rNoBody.Message!);
    }

    [Fact]
    public void MapIngestBinExit_exit5_is_the_clear_too_large_message_not_a_503()
    {
        // FIX 2: extracted text over MaxDocChars exits 5 — the SAME "document too large" contract as the text path.
        // It must surface that message (content was NOT silently truncated to MAX_CHUNKS), not the embed-down 503.
        const string stdout = "{\"error\":\"document too large (260000 chars) — split it or attach a smaller file (max 200000 chars).\"}";
        var r = DocSearch.MapIngestBinExit(5, stdout);
        Assert.False(r.Ok);
        Assert.Equal(0, r.Chunks);
        Assert.Contains("too large", r.Message!);
        Assert.DoesNotContain("embed server", r.Message!);

        // with no parseable body, exit 5 still surfaces a clear "too large" default (never the embed-server message).
        var rNoBody = DocSearch.MapIngestBinExit(5, "");
        Assert.False(rNoBody.Ok);
        Assert.Contains("too large", rNoBody.Message!);
        Assert.DoesNotContain("embed server", rNoBody.Message!);
    }

    [Fact]
    public void MapIngestBinExit_generic_nonzero_names_both_offline_parsers_and_a_down_embed_server()
    {
        // FIX 5: the realistic live failure on the binary path is `uv` failing to RESOLVE pypdf/python-docx OFFLINE
        // (a non-3/4/5 exit), which the old code mislabeled "start the embed server". The generic fallback must name
        // BOTH likely causes (parsers unavailable offline OR embed server down) so the message isn't embed-specific.
        var r = DocSearch.MapIngestBinExit(1, "");
        Assert.False(r.Ok);
        Assert.Equal(0, r.Chunks);
        Assert.Contains("parser", r.Message!, System.StringComparison.OrdinalIgnoreCase);   // names the offline-parsers cause
        Assert.Contains("embed", r.Message!, System.StringComparison.OrdinalIgnoreCase);    // still names the embed-server cause
    }

    // ---- ParseError (FIX 7): the pure {"error":…}-extracting seam doc_ingest_bin's error exits print. A valid
    //      object yields the string; non-JSON / a non-string error / a missing key yields null (the caller then
    //      uses its own default). Pure, so locked here with no process. ----
    [Fact]
    public void ParseError_reads_a_valid_error_object_into_its_string()
    {
        Assert.Equal("boom", DocSearch.ParseError("{\"error\":\"boom\"}"));
        Assert.Equal("couldn't read this file", DocSearch.ParseError("{\"error\":\"couldn't read this file\"}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]               // no "error" key
    [InlineData("{\"error\":123}")]  // "error" is not a string
    [InlineData("[]")]               // not an object
    public void ParseError_returns_null_on_missing_or_malformed_input(string json)
        => Assert.Null(DocSearch.ParseError(json));

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
