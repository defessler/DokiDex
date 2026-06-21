using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The persistent-memory recall sidecar (serving/memory-mcp/memory_db.py via `uv run python`). The live exec is an
// integration concern that degrades to EMPTY when the sidecar / uv / python is absent (the no-memory path, so chat
// is byte-for-byte unchanged) — exactly DocSearch.RetrieveAsync's contract. The PURE seams (BuildRecentArgs /
// BuildSearchArgs / ParseMemoryJson / ToMemoryNotes) are locked here with no process, mirroring DocSearchTests.
public class MemoryRecallTests
{
    [Fact]
    public void BuildRecentArgs_targets_memory_db_recent_with_a_clamped_limit()
    {
        var args = MemoryRecall.BuildRecentArgs(12);
        Assert.EndsWith("memory_db.py", args[0]);
        Assert.Equal("recent", args[1]);
        Assert.Equal("12", args[2]);
        Assert.Equal("50", MemoryRecall.BuildRecentArgs(999)[2]);   // clamp high
        Assert.Equal("1", MemoryRecall.BuildRecentArgs(0)[2]);      // clamp low
    }

    [Fact]
    public void BuildSearchArgs_targets_memory_db_search_with_query_and_clamped_limit()
    {
        var args = MemoryRecall.BuildSearchArgs("my gpu", 5);
        Assert.EndsWith("memory_db.py", args[0]);
        Assert.Equal("search", args[1]);
        Assert.Equal("my gpu", args[2]);
        Assert.Equal("5", args[3]);
    }

    [Fact]
    public void ParseMemoryJson_extracts_content_strings_and_skips_blanks()
    {
        var json = "[{\"id\":1,\"content\":\"the GPU has 32GB\",\"tags\":\"hw\"}," +
                   "{\"id\":2,\"content\":\"  \",\"tags\":\"\"}," +
                   "{\"id\":3,\"content\":\"Crush is the coder CLI\"}]";
        var rows = MemoryRecall.ParseMemoryJson(json);
        Assert.Equal(2, rows.Count);   // the blank-content row is skipped
        Assert.Contains("the GPU has 32GB", rows);
        Assert.Contains("Crush is the coder CLI", rows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    public void ParseMemoryJson_degrades_to_empty_on_bad_input(string? json)
        => Assert.Empty(MemoryRecall.ParseMemoryJson(json));

    [Fact]
    public void ToMemoryNotes_maps_contents_to_keyless_memory_notes()
    {
        var notes = MemoryRecall.ToMemoryNotes(new[] { "fact one", "fact two" });
        Assert.Equal(2, notes.Count);
        Assert.Equal("", notes[0].Key);          // memory_db content is the fact; no separate key
        Assert.Equal("fact one", notes[0].Value);
    }

    [Fact]
    public void ToMemoryNotes_empty_in_empty_out()
        => Assert.Empty(MemoryRecall.ToMemoryNotes(System.Array.Empty<string>()));

    // ---- Editable-memory admin seams (the "editable memory agent" save/list/delete the /api/memory endpoints +
    //      a future panel use). Pure argv/parse seams locked here; the live shell is integration (degrades). ----

    [Fact]
    public void BuildSaveArgs_targets_memory_db_save_with_content_and_tags()
    {
        var args = MemoryRecall.BuildSaveArgs("Doug prefers terse answers", "pref");
        Assert.EndsWith("memory_db.py", args[0]);
        Assert.Equal("save", args[1]);
        Assert.Equal("Doug prefers terse answers", args[2]);
        Assert.Equal("pref", args[3]);
    }

    [Fact]
    public void BuildDeleteArgs_targets_memory_db_delete_with_the_id()
    {
        var args = MemoryRecall.BuildDeleteArgs(42);
        Assert.EndsWith("memory_db.py", args[0]);
        Assert.Equal("delete", args[1]);
        Assert.Equal("42", args[2]);
    }

    [Fact]
    public void ParseSavedId_reads_the_new_row_id_or_zero()
    {
        Assert.Equal(7, MemoryRecall.ParseSavedId("{\"id\":7}"));
        Assert.Equal(0, MemoryRecall.ParseSavedId("nope"));
        Assert.Equal(0, MemoryRecall.ParseSavedId(null));
    }

    [Fact]
    public void ParseMemoryRecords_reads_id_content_tags_for_the_editable_list()
    {
        var json = "[{\"id\":3,\"content\":\"fact A\",\"tags\":\"t1\",\"ts\":1.0}," +
                   "{\"id\":1,\"content\":\"fact B\",\"tags\":\"\"}," +
                   "{\"id\":9,\"content\":\"  \",\"tags\":\"x\"}]";
        var recs = MemoryRecall.ParseMemoryRecords(json);
        Assert.Equal(2, recs.Count);                 // the blank-content row is skipped
        Assert.Equal(3, recs[0].Id);
        Assert.Equal("fact A", recs[0].Content);
        Assert.Equal("t1", recs[0].Tags);
        Assert.Equal(1, recs[1].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"x\":1}")]
    public void ParseMemoryRecords_degrades_to_empty(string? json)
        => Assert.Empty(MemoryRecall.ParseMemoryRecords(json));
}
