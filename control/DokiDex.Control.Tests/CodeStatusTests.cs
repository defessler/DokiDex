using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Pure JSON-shape parsers backing `/status` (1.6 — folds in the old 1.8 /status per the F3 merge). `doki code`
// probes llama-swap DIRECTLY (Program.cs's own HttpClient GETs against :8080/running and :8080/v1/models) rather
// than shelling out to `doki status`, so these mirror StatusProbe.LlamaSwapInfoAsync's parsing (DokiDex.Control.
// Services) minimally — total + side-effect-free, no network, no dependency on the WPF control-plane's StatusDoc
// model types.
public class CodeStatusTests
{
    [Fact]
    public void ParseRunning_reads_the_running_array_shape()
    {
        var info = CodeStatus.ParseRunning("""{"running":[{"model":"coder-fast","state":"ready"}]}""");
        Assert.Equal("coder-fast", info.Model);
        Assert.Equal("ready", info.State);
    }

    [Fact]
    public void ParseRunning_falls_back_to_root_object_fields()
    {
        // Some llama-swap builds return the object's fields directly at the root instead of wrapping in "running".
        var info = CodeStatus.ParseRunning("""{"model":"coder-big","state":"loading"}""");
        Assert.Equal("coder-big", info.Model);
        Assert.Equal("loading", info.State);
    }

    [Fact]
    public void ParseRunning_yields_nulls_on_an_empty_running_array()
    {
        var info = CodeStatus.ParseRunning("""{"running":[]}""");
        Assert.Null(info.Model);
        Assert.Null(info.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ParseRunning_degrades_to_nulls_never_throws(string? json)
    {
        var info = CodeStatus.ParseRunning(json);
        Assert.Null(info.Model);
        Assert.Null(info.State);
    }

    [Fact]
    public void ParseModels_reads_the_data_array_ids_in_order()
    {
        var models = CodeStatus.ParseModels("""{"data":[{"id":"coder-fast"},{"id":"coder-big"}]}""");
        Assert.Equal(new[] { "coder-fast", "coder-big" }, models);
    }

    [Fact]
    public void ParseModels_skips_entries_with_no_string_id()
    {
        var models = CodeStatus.ParseModels("""{"data":[{"id":"coder-fast"},{"nope":true},{"id":123}]}""");
        Assert.Single(models);
        Assert.Equal("coder-fast", models[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"data":"nope"}""")]
    public void ParseModels_degrades_to_empty_never_throws(string? json)
        => Assert.Empty(CodeStatus.ParseModels(json));
}
