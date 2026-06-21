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
}
