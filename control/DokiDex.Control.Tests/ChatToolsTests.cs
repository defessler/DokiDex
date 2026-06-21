using System.Linq;
using System.Text.Json;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The curated tool registry for the agent loop (ONE tool this slice: search_library). Two seams kept pure +
// tested with NO GPU/disk: (a) the JSON schema object placed in the request 'tools' array is well-formed
// OpenAI function-tool shape, and (b) Run's arg-parse + unknown-tool fall-through. The search_library happy
// path itself touches the gallery on disk (GalleryService.List) and is exercised only thinly here — the
// fragile parsing is what's locked. Mirrors the small-curated-toolset / bounded-loop mitigation in decisions.md.
//
// [Collection("PendingGenStore")] — the generate_image Run tests WRITE to the shared on-disk pending-gen/ dir
// under RepoPaths.Root. xUnit parallelizes test CLASSES, so without serializing these against PendingGenStoreTests
// a concurrent Enqueue/Delete could interleave with this class's queue-and-cleanup and flap. The collection makes
// the two store-writing classes run serially (identity asserts already guard within-class).
[Collection("PendingGenStore")]
public class ChatToolsTests
{
    [Fact]
    public void The_tools_array_is_well_formed_openai_function_shape()
    {
        // Round-trip the schema the request will carry and assert the OpenAI tool contract:
        //   [{ type:"function", function:{ name, description, parameters:{ type:object, properties:{query} } } }]
        var json = JsonSerializer.Serialize(ChatTools.ToolsJson);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(5, root.GetArrayLength());   // gated 1 -> 5: + generate_image + edit_image (the write-action tools)

        // EVERY tool is a well-formed OpenAI function schema with a required string param the model fills first:
        // the three READ tools take {query:string}; the two WRITE tools (generate_image / edit_image) take {prompt:string}.
        foreach (var tool in root.EnumerateArray())
        {
            Assert.Equal("function", tool.GetProperty("type").GetString());
            var fn = tool.GetProperty("function");
            Assert.False(string.IsNullOrWhiteSpace(fn.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fn.GetProperty("description").GetString()));
            var p = fn.GetProperty("parameters");
            Assert.Equal("object", p.GetProperty("type").GetString());
            var props = p.GetProperty("properties");
            var key = props.TryGetProperty("query", out var q) ? q : props.GetProperty("prompt");
            Assert.Equal("string", key.GetProperty("type").GetString());
        }

        var names = root.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "search_library", "web_search", "code_search", "generate_image", "edit_image" }, names);
    }

    [Fact]
    public void generate_image_schema_has_a_required_prompt_and_the_optional_gen_fields()
    {
        // The write-action tool's params map 1:1 onto GenRequest(Prompt, Kind, Model, Count): a REQUIRED prompt
        // plus optional kind/model/count. The required array names exactly prompt (the others default in MapGenArgs).
        var json = JsonSerializer.Serialize(ChatTools.GenerateImageSchema);
        using var doc = JsonDocument.Parse(json);
        var fn = doc.RootElement.GetProperty("function");
        Assert.Equal("generate_image", fn.GetProperty("name").GetString());
        var p = fn.GetProperty("parameters");
        var props = p.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("prompt").GetProperty("type").GetString());
        Assert.True(props.TryGetProperty("kind", out _));
        Assert.True(props.TryGetProperty("model", out _));
        Assert.Equal("integer", props.GetProperty("count").GetProperty("type").GetString());
        var required = p.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "prompt" }, required);
    }

    [Fact]
    public void Run_with_an_unknown_tool_name_returns_a_clear_unknown_tool_message_listing_all_tools()
    {
        var result = ChatTools.Run("get_weather", "{\"city\":\"Tokyo\"}");
        Assert.Contains("unknown tool", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("get_weather", result);
        // The default text now lists the full curated set so the model can re-plan.
        Assert.Contains("search_library", result);
        Assert.Contains("web_search", result);
        Assert.Contains("code_search", result);
        Assert.Contains("generate_image", result);
    }

    [Theory]
    // defaults: missing kind => "image", missing model => null, missing count => 1
    [InlineData("{\"prompt\":\"a cat\"}", "a cat", "image", null, 1)]
    [InlineData("{\"prompt\":\"  spaced  \",\"kind\":\"edit\",\"model\":\"sdxl\",\"count\":3}", "spaced", "edit", "sdxl", 3)]
    // kind clamped to the image family: video/music/i2v/foley => "image"
    [InlineData("{\"prompt\":\"x\",\"kind\":\"video\"}", "x", "image", null, 1)]
    [InlineData("{\"prompt\":\"x\",\"kind\":\"EDIT\"}", "x", "edit", null, 1)]   // case-insensitive
    // count clamped to 1..9
    [InlineData("{\"prompt\":\"x\",\"count\":99}", "x", "image", null, 9)]
    [InlineData("{\"prompt\":\"x\",\"count\":0}", "x", "image", null, 1)]
    [InlineData("{\"prompt\":\"x\",\"count\":-5}", "x", "image", null, 1)]
    // tolerant of a string-typed count (some models emit "count":"4")
    [InlineData("{\"prompt\":\"x\",\"count\":\"4\"}", "x", "image", null, 4)]
    public void MapGenArgs_parses_defaults_and_clamps(string args, string ep, string ek, string? em, int ec)
    {
        var (prompt, kind, model, count) = ChatTools.MapGenArgs(args);
        Assert.Equal(ep, prompt);
        Assert.Equal(ek, kind);
        Assert.Equal(em, model);
        Assert.Equal(ec, count);
    }

    [Theory]
    [InlineData("{\"prompt\":\"   \"}")]   // whitespace prompt
    [InlineData("{\"kind\":\"image\"}")]   // missing prompt
    [InlineData("")]                        // no arguments at all
    [InlineData("not json")]                // malformed
    public void MapGenArgs_blank_or_missing_prompt_yields_empty_prompt(string args)
    {
        var (prompt, _, _, _) = ChatTools.MapGenArgs(args);
        Assert.Equal("", prompt);   // the executor turns an empty prompt into the "need a prompt" line, never throws
    }

    [Fact]
    public void FormatGenQueued_is_bounded_and_interpolates_count_and_kind()
    {
        var one = ChatTools.FormatGenQueued(1, "image");
        Assert.Contains("1 image", one);
        Assert.Contains("Media mode", one, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(one.Length < 300, $"queued result should stay bounded, was {one.Length}");

        var many = ChatTools.FormatGenQueued(5, "edit");
        Assert.Contains("5 edit", many);
    }

    [Fact]
    public void Run_for_generate_image_with_a_prompt_queues_and_returns_the_bounded_notice()
    {
        // The disk touch (PendingGenStore.Enqueue) is graceful; a present prompt returns the queued notice and
        // names the Media-mode switch. We clean up any file this enqueues so the test leaves no residue.
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        var result = ChatTools.Run("generate_image", "{\"prompt\":\"a neon dragon\",\"count\":2}");
        Assert.Contains("Media mode", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Queued 2", result);   // the surfaced phrase (FormatGenQueued), not a position-blind stray '2'
        foreach (var p in PendingGenStore.List())
            if (!before.Contains(p.Id)) PendingGenStore.Delete(p.Id);
    }

    [Fact]
    public void Run_for_generate_image_threads_the_conversation_id_into_the_queued_pending_gen()
    {
        // The render round-trip (P1) needs a finished gen to map back to the chat thread it was requested in.
        // Run must thread the originating conversation id into the PendingGen.Conversation backlink — it was
        // hardcoded null, so a completed gen could never be surfaced inline in its conversation.
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        var convId = "conv-" + System.Guid.NewGuid().ToString("N")[..8];
        ChatTools.Run("generate_image", "{\"prompt\":\"a neon dragon\"}", convId);
        var created = PendingGenStore.List().Where(p => !before.Contains(p.Id)).ToList();
        try
        {
            Assert.Single(created);
            Assert.Equal(convId, created[0].Conversation);
        }
        finally { foreach (var p in created) PendingGenStore.Delete(p.Id); }
    }

    [Fact]
    public void Run_for_generate_image_without_a_conversation_leaves_the_backlink_null()
    {
        // Backward-compatible default: the 2-arg Run (no conversation) still enqueues a null backlink (a stateless
        // caller / non-chat dispatch), so the new overload can't silently fabricate a wrong link.
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        ChatTools.Run("generate_image", "{\"prompt\":\"a quiet meadow\"}");
        var created = PendingGenStore.List().Where(p => !before.Contains(p.Id)).ToList();
        try
        {
            Assert.Single(created);
            Assert.Null(created[0].Conversation);
        }
        finally { foreach (var p in created) PendingGenStore.Delete(p.Id); }
    }

    [Fact]
    public void Run_for_generate_image_with_a_blank_prompt_asks_for_a_prompt_and_does_not_queue()
    {
        // IDENTITY snapshot, not a raw global Count: PendingGenStore.List() reads the shared on-disk pending-gen/
        // dir under RepoPaths.Root and xUnit runs test CLASSES in parallel, so a concurrent Enqueue (e.g.
        // PendingGenStoreTests) would move a plain before/after Count and spuriously fail. Snapshot the set of ids
        // before, then assert NO NEW id appeared — robust no matter how many records a sibling class adds.
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        var result = ChatTools.Run("generate_image", "{\"prompt\":\"   \"}");
        Assert.Contains("prompt", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PendingGenStore.List(), p => !before.Contains(p.Id));   // this call queued nothing
    }

    // ----- edit_image: the refine/img2img sibling of generate_image. Same QUEUE-AND-NOTIFY contract, but it
    // carries an init image + a strength so the deferred renderer runs img2img. The pure seams (MapEditArgs arg
    // parse, ResolveEditSource default-source pick) are tested with NO disk; the Run path round-trips the store. -----

    [Fact]
    public void edit_image_schema_has_a_required_prompt_and_optional_source_and_strength()
    {
        // edit_image's params: a REQUIRED prompt (the edit instruction) + optional source (gallery-relative image
        // name) + optional strength (the img2img "vary" dial). required names exactly prompt; the rest default in
        // MapEditArgs (source => null => resolve to the most-recent done gen for this conversation; strength => 0.6).
        var json = JsonSerializer.Serialize(ChatTools.EditImageSchema);
        using var doc = JsonDocument.Parse(json);
        var fn = doc.RootElement.GetProperty("function");
        Assert.Equal("edit_image", fn.GetProperty("name").GetString());
        var p = fn.GetProperty("parameters");
        var props = p.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("prompt").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("strength").GetProperty("type").GetString());
        var required = p.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "prompt" }, required);
    }

    [Fact]
    public void The_tools_array_now_includes_edit_image_as_the_curated_fifth_tool()
    {
        // Gated 4 -> 5: edit_image is the justified sibling of generate_image (refine/img2img), not sprawl. The
        // request 'tools' array must carry it last, well-formed, so the LLM can select it.
        var json = JsonSerializer.Serialize(ChatTools.ToolsJson);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(5, root.GetArrayLength());
        var names = root.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "search_library", "web_search", "code_search", "generate_image", "edit_image" }, names);
    }

    [Theory]
    // defaults: missing source => null (resolved later), missing strength => 0.6
    [InlineData("{\"prompt\":\"make it bluer\"}", "make it bluer", null, 0.6)]
    // source trimmed; explicit strength passes through
    [InlineData("{\"prompt\":\"  same pose  \",\"source\":\"  2026/06/img_001.png  \",\"strength\":0.35}", "same pose", "2026/06/img_001.png", 0.35)]
    // tolerant of a string-typed strength (some models emit "strength":"0.4")
    [InlineData("{\"prompt\":\"x\",\"strength\":\"0.4\"}", "x", null, 0.4)]
    // strength clamped to (0,1]: out-of-band high => 1, zero/negative => the 0.6 default (an invalid dial is ignored)
    [InlineData("{\"prompt\":\"x\",\"strength\":5}", "x", null, 1.0)]
    [InlineData("{\"prompt\":\"x\",\"strength\":0}", "x", null, 0.6)]
    [InlineData("{\"prompt\":\"x\",\"strength\":-2}", "x", null, 0.6)]
    // blank source => null (not "")
    [InlineData("{\"prompt\":\"x\",\"source\":\"   \"}", "x", null, 0.6)]
    public void MapEditArgs_parses_prompt_source_and_strength_with_defaults_and_clamp(string args, string ep, string? es, double estr)
    {
        var (prompt, source, strength) = ChatTools.MapEditArgs(args);
        Assert.Equal(ep, prompt);
        Assert.Equal(es, source);
        Assert.Equal(estr, strength, 3);
    }

    [Theory]
    [InlineData("{\"prompt\":\"   \"}")]   // whitespace prompt
    [InlineData("{\"source\":\"x.png\"}")] // missing prompt
    [InlineData("")]                        // no arguments at all
    [InlineData("not json")]                // malformed
    public void MapEditArgs_blank_or_missing_prompt_yields_empty_prompt(string args)
    {
        var (prompt, _, _) = ChatTools.MapEditArgs(args);
        Assert.Equal("", prompt);   // the executor turns an empty prompt into the "need a prompt" line, never throws
    }

    [Fact]
    public void ResolveEditSource_prefers_an_explicit_source_verbatim()
    {
        // An explicit source always wins — the conversation-history fallback is only consulted when none was given.
        var list = new[]
        {
            new PendingGen("id1", "p", "image", null, 1, "2026-06-21T00:00:02Z", "conv-1", Status: "done", ResultRel: "newer.png"),
        };
        Assert.Equal("explicit.png", ChatTools.ResolveEditSource("explicit.png", "conv-1", list));
    }

    [Fact]
    public void ResolveEditSource_defaults_to_the_most_recent_done_gen_for_this_conversation()
    {
        // No explicit source => pick the newest DONE PendingGen in THIS conversation that has a ResultRel. List()
        // is newest-first, so the first matching record's ResultRel is the most-recent finished image to refine.
        var list = new[]
        {
            new PendingGen("id3", "p", "image", null, 1, "t", "conv-1", Status: "done",      ResultRel: "newest.png"),
            new PendingGen("id2", "p", "image", null, 1, "t", "conv-1", Status: "done",      ResultRel: "older.png"),
            new PendingGen("id1", "p", "image", null, 1, "t", "conv-1", Status: "rendering", ResultRel: null),
        };
        Assert.Equal("newest.png", ChatTools.ResolveEditSource(null, "conv-1", list));
    }

    [Fact]
    public void ResolveEditSource_ignores_other_conversations_and_not_done_or_resultless_records()
    {
        // The fallback must be conversation-scoped and only consider finished gens with a real ResultRel: a done
        // gen in ANOTHER conversation, a queued/rendering gen, and a done gen with no ResultRel are all skipped.
        var list = new[]
        {
            new PendingGen("id4", "p", "image", null, 1, "t", "conv-2", Status: "done",      ResultRel: "other-conv.png"),
            new PendingGen("id3", "p", "image", null, 1, "t", "conv-1", Status: "queued",    ResultRel: null),
            new PendingGen("id2", "p", "image", null, 1, "t", "conv-1", Status: "done",      ResultRel: null),
            new PendingGen("id1", "p", "image", null, 1, "t", "conv-1", Status: "done",      ResultRel: "mine.png"),
        };
        Assert.Equal("mine.png", ChatTools.ResolveEditSource(null, "conv-1", list));
        // Nothing usable at all => null (the executor turns that into the "need a source" line).
        Assert.Null(ChatTools.ResolveEditSource(null, "conv-empty", list));
        Assert.Null(ChatTools.ResolveEditSource(null, null, list));   // a null conversation never matches a backlink
    }

    [Fact]
    public void FormatEditQueued_is_bounded_and_names_the_source_and_media_mode()
    {
        var text = ChatTools.FormatEditQueued("2026/06/img_001.png");
        Assert.Contains("img_001.png", text);
        Assert.Contains("Media mode", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Length < 300, $"queued edit notice should stay bounded, was {text.Length}");
    }

    [Fact]
    public void Run_for_edit_image_with_a_prompt_and_source_queues_a_pending_gen_with_initimage_and_strength()
    {
        // The img2img round-trip: an edit_image call must persist a PendingGen carrying InitImage (the source to
        // refine) + Strength (the vary dial), kind "image", count 1, and the conversation backlink. Store round-trip
        // mirrors the generate_image tests; clean up whatever this enqueues.
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        var convId = "conv-" + System.Guid.NewGuid().ToString("N")[..8];
        var result = ChatTools.Run("edit_image",
            "{\"prompt\":\"make it bluer\",\"source\":\"2026/06/img_001.png\",\"strength\":0.4}", convId);
        var created = PendingGenStore.List().Where(p => !before.Contains(p.Id)).ToList();
        try
        {
            Assert.Contains("Media mode", result, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(created);
            var rec = created[0];
            Assert.Equal("make it bluer", rec.Prompt);
            Assert.Equal("image", rec.Kind);            // edit_image queues into the image family
            Assert.Equal(1, rec.Count);
            Assert.Equal("2026/06/img_001.png", rec.InitImage);
            Assert.Equal(0.4, rec.Strength!.Value, 3);
            Assert.Equal(convId, rec.Conversation);     // backlink threaded for the inline-surface round-trip
        }
        finally { foreach (var p in created) PendingGenStore.Delete(p.Id); }
    }

    [Fact]
    public void Run_for_edit_image_defaults_the_source_to_the_most_recent_done_gen_in_the_conversation()
    {
        // No explicit source: edit_image resolves it from history — the newest done gen in THIS conversation. We
        // seed one done PendingGen with a ResultRel, then edit with no source and assert the queued edit's InitImage
        // picked it up. Both records are cleaned up.
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        var convId = "conv-" + System.Guid.NewGuid().ToString("N")[..8];
        var seed = PendingGenStore.Enqueue("a neon dragon", "image", null, 1, convId);
        PendingGenStore.SetStatus(seed.Id, "done", resultRel: "2026/06/seeded.png");
        try
        {
            ChatTools.Run("edit_image", "{\"prompt\":\"same dragon, new pose\"}", convId);
            var edit = PendingGenStore.List().Single(p => p.Conversation == convId && p.InitImage == "2026/06/seeded.png");
            Assert.Equal("same dragon, new pose", edit.Prompt);
            Assert.NotNull(edit.Strength);   // the default vary dial was applied
        }
        finally
        {
            foreach (var p in PendingGenStore.List().Where(p => !before.Contains(p.Id))) PendingGenStore.Delete(p.Id);
        }
    }

    [Fact]
    public void Run_for_edit_image_with_a_blank_prompt_asks_for_a_prompt_and_does_not_queue()
    {
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        var result = ChatTools.Run("edit_image", "{\"prompt\":\"   \",\"source\":\"x.png\"}");
        Assert.Contains("edit instruction", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PendingGenStore.List(), p => !before.Contains(p.Id));   // queued nothing
    }

    [Fact]
    public void Run_for_edit_image_with_no_source_and_no_history_asks_for_a_source_and_does_not_queue()
    {
        // A present prompt but NO source and an empty conversation history => the tool can't guess what to edit, so
        // it returns a clear "which image" line WITHOUT touching disk (a fresh conversation id has zero done gens).
        var before = PendingGenStore.List().Select(p => p.Id).ToHashSet();
        var convId = "conv-" + System.Guid.NewGuid().ToString("N")[..8];   // no done gens => nothing to default to
        var result = ChatTools.Run("edit_image", "{\"prompt\":\"make it bluer\"}", convId);
        Assert.Contains("image", result, System.StringComparison.OrdinalIgnoreCase);   // names that it needs a source image
        Assert.DoesNotContain(PendingGenStore.List(), p => !before.Contains(p.Id));     // queued nothing
    }

    [Fact]
    public void Run_with_an_unknown_tool_name_lists_edit_image_in_the_curated_set()
    {
        // The unknown-tool fall-through advertises the full curated set so the model can re-plan onto edit_image.
        var result = ChatTools.Run("frobnicate", "{}");
        Assert.Contains("edit_image", result);
    }

    [Theory]
    [InlineData("{\"query\":\"x\",\"k\":3}", "x", 3)]
    [InlineData("{\"query\":\"x\"}", "x", 5)]                 // default k when omitted
    [InlineData("{\"query\":\"x\",\"k\":\"4\"}", "x", 4)]     // tolerant: a string-typed int
    [InlineData("not json", "", 5)]                            // malformed => empty query + default k
    public void ParseQueryAndK_extracts_query_and_optional_k(string args, string eq, int ek)
    {
        var (q, k) = ChatTools.ParseQueryAndK(args, 5);
        Assert.Equal(eq, q);
        Assert.Equal(ek, k);
    }

    // The sidecar-backed tools (web_search / code_search) used to be exercised by two Run-dispatch tests that
    // SPAWNED A REAL SUBPROCESS (uvx ddgs / uv run python ...): live network + a :8090 call on any box where the
    // tools are present — slow, flaky, and ZERO signal on the degrade/format path they were named for. They are
    // replaced here by HERMETIC coverage of the pure Result -> tool-text decision (ChatTools.FormatToolResult),
    // which is the exact guard logic Run runs after the (out-of-scope) exec returns. No process is spawned.

    [Fact]
    public void FormatToolResult_on_ok_but_empty_returns_the_clean_no_results_line_not_the_done_sentinel()
    {
        // FIX 2: a search that RAN FINE but matched nothing comes back Ok=true, Rows=[], Message="done". The
        // model must see the formatter's clean "no results" line — NOT the raw "done" sentinel. The success
        // branch ignores Message entirely and surfaces the formatted (empty => "no results") text.
        var formatted = WebSearch.FormatWebResults("nothing here", System.Array.Empty<(string, string, string)>());
        var text = ChatTools.FormatToolResult(ok: true, rowCount: 0, message: "done", formatted: formatted);

        Assert.Equal(formatted, text);
        Assert.Contains("no web results", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("done", text);   // the bare sentinel must never reach the model
    }

    [Fact]
    public void FormatToolResult_on_ok_with_rows_surfaces_the_formatted_results()
    {
        // Ok + rows => the formatted block verbatim (Message is irrelevant on the success path).
        var formatted = "2 web result(s) for \"x\": ...";
        var text = ChatTools.FormatToolResult(ok: true, rowCount: 2, message: "done", formatted: formatted);
        Assert.Equal(formatted, text);
    }

    [Fact]
    public void FormatToolResult_on_genuine_failure_surfaces_the_degrade_message_verbatim()
    {
        // Only a real !Ok (sidecar / embed server / network down) surfaces the specific degrade Message so the
        // model learns WHY the tool was unavailable (e.g. "start the embed server ..."), rather than a bland line.
        const string degrade = "code search unavailable — start the embed server (start-embed.ps1) and build the index (doki index).";
        var formatted = CodeSearch.FormatCodeResults("loop", System.Array.Empty<(string, int, int, string, double)>());
        var text = ChatTools.FormatToolResult(ok: false, rowCount: 0, message: degrade, formatted: formatted);
        Assert.Equal(degrade, text);
    }

    [Fact]
    public void FormatToolResult_on_failure_with_no_message_falls_back_to_the_clean_formatted_line()
    {
        // A !Ok with no specific message still degrades gracefully to the formatter's clean "no results" line
        // (never blank, never a throw) — the agent loop always gets usable tool text.
        var formatted = WebSearch.FormatWebResults("q", System.Array.Empty<(string, string, string)>());
        Assert.Equal(formatted, ChatTools.FormatToolResult(ok: false, rowCount: 0, message: null, formatted: formatted));
        Assert.Equal(formatted, ChatTools.FormatToolResult(ok: false, rowCount: 0, message: "   ", formatted: formatted));
    }

    [Theory]
    [InlineData("{\"query\":\"neon dragon\"}", "neon dragon")]
    [InlineData("{ \"query\" : \"  spaced  \" }", "spaced")]
    [InlineData("{\"other\":\"x\"}", "")]   // missing query => empty (a blank query lists everything)
    [InlineData("", "")]                    // no arguments at all => empty query, never throws
    [InlineData("not json", "")]            // malformed arguments => empty query, graceful
    public void ParseQuery_extracts_the_trimmed_query_or_empty(string argumentsJson, string expected)
        => Assert.Equal(expected, ChatTools.ParseQuery(argumentsJson));

    [Fact]
    public void Run_for_search_library_never_throws_and_returns_text()
    {
        // The disk call (GalleryService.List) is thin; with no gallery present it degrades to a clear "no
        // matching" text rather than throwing. Locks that Run is total even when the library is empty.
        var result = ChatTools.Run("search_library", "{\"query\":\"zzz-unlikely-match-zzz\"}");
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void A_long_item_prompt_is_truncated_so_the_tool_result_stays_bounded()
    {
        // B1: long SDXL prompts re-sent across up to MaxToolHops+1 hops would bloat context past max_tokens. Each
        // item's prompt is capped (first ~120 chars + an ellipsis) so the whole tool result stays small. The pure
        // formatter is exercised directly with a single very long prompt so the bound is deterministic (no disk).
        var longPrompt = new string('x', 5000);
        var text = ChatTools.FormatSearchResults("dragon", new[] { ("img_001.png", longPrompt) });

        // The full 5000-char prompt must NOT survive verbatim; the rendered prompt is capped to the per-item budget.
        Assert.DoesNotContain(longPrompt, text);
        Assert.True(text.Length < 400, $"tool result should stay small, was {text.Length} chars");
        Assert.Contains("img_001.png", text);   // the item is still listed, just with a truncated prompt
        Assert.Contains("…", text);             // the truncation marker is present
    }

    [Fact]
    public void A_short_item_prompt_is_left_intact()
    {
        // Below the cap, the prompt is shown verbatim with no truncation marker (the common case must be lossless).
        var text = ChatTools.FormatSearchResults("dragon", new[] { ("img_001.png", "a neon dragon at night") });
        Assert.Contains("a neon dragon at night", text);
        Assert.DoesNotContain("…", text);
    }
}
