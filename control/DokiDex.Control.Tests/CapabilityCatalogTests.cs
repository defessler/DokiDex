using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Pure unit tests for CapabilityCatalog: JSON parsing and the static fallback.
// No shell, no GPU, no disk access. Mirrors the contract in Get-GenKindCatalog (serving/doki-gen.ps1).
public class CapabilityCatalogTests
{
    [Fact]
    public void ParseJson_parses_a_valid_list_kinds_output()
    {
        // Exact shape emitted by `doki gen x -ListKinds` (compact single-line JSON array)
        const string json = "[{\"id\":\"image\",\"label\":\"image\",\"group\":\"image\",\"ready\":true,\"requires\":null},{\"id\":\"video\",\"label\":\"video\",\"group\":\"video\",\"ready\":true,\"requires\":null},{\"id\":\"music\",\"label\":\"music\",\"group\":\"audio\",\"ready\":true,\"requires\":null},{\"id\":\"edit\",\"label\":\"edit\",\"group\":\"image\",\"ready\":true,\"requires\":null},{\"id\":\"i2v\",\"label\":\"i2v\",\"group\":\"video\",\"ready\":true,\"requires\":null},{\"id\":\"foley\",\"label\":\"foley\",\"group\":\"video\",\"ready\":true,\"requires\":null},{\"id\":\"ltx\",\"label\":\"video + audio\",\"group\":\"video\",\"ready\":true,\"requires\":null},{\"id\":\"faceid\",\"label\":\"face id\",\"group\":\"image\",\"ready\":false,\"requires\":\"setup.ps1 -FaceId\"},{\"id\":\"pulid\",\"label\":\"face id (flux)\",\"group\":\"image\",\"ready\":false,\"requires\":\"setup.ps1 -Pulid\"},{\"id\":\"infinitetalk\",\"label\":\"talking video\",\"group\":\"video\",\"ready\":false,\"requires\":\"setup.ps1 -InfiniteTalk\"},{\"id\":\"latentsync\",\"label\":\"lip sync\",\"group\":\"video\",\"ready\":false,\"requires\":\"setup.ps1 -LatentSync\"},{\"id\":\"speech\",\"label\":\"speech\",\"group\":\"audio\",\"ready\":false,\"requires\":\"setup.ps1 -TtsSuite\"}]";
        var kinds = CapabilityCatalog.ParseJson(json);
        Assert.NotNull(kinds);
        Assert.Equal(12, kinds!.Count);

        Assert.Equal("image", kinds[0].Id);
        Assert.Equal("image", kinds[0].Label);
        Assert.Equal("image", kinds[0].Group);
        Assert.True(kinds[0].Ready);
        Assert.Null(kinds[0].Requires);

        Assert.Equal("ltx", kinds[6].Id);
        Assert.Equal("video + audio", kinds[6].Label);
        Assert.Equal("video", kinds[6].Group);
        Assert.True(kinds[6].Ready);
        Assert.Null(kinds[6].Requires);

        Assert.Equal("faceid", kinds[7].Id);
        Assert.Equal("face id", kinds[7].Label);
        Assert.Equal("image", kinds[7].Group);
        Assert.False(kinds[7].Ready);
        Assert.Equal("setup.ps1 -FaceId", kinds[7].Requires);

        Assert.Equal("speech", kinds[11].Id);
        Assert.Equal("speech", kinds[11].Label);
        Assert.Equal("audio", kinds[11].Group);
        Assert.False(kinds[11].Ready);
        Assert.Equal("setup.ps1 -TtsSuite", kinds[11].Requires);
    }

    [Fact]
    public void ParseJson_scans_bottom_to_top_finds_json_past_preamble_lines()
    {
        // Simulate pwsh output with preamble lines before the JSON (e.g. a verbose message)
        const string stdout = "loading doki-gen...\n[{\"id\":\"image\",\"label\":\"image\",\"group\":\"image\",\"ready\":true,\"requires\":null}]\n";
        var kinds = CapabilityCatalog.ParseJson(stdout);
        Assert.NotNull(kinds);
        Assert.Single(kinds!);
        Assert.Equal("image", kinds![0].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"object\": true}")]   // JSON object, not array
    [InlineData("[]")]                    // empty array
    public void ParseJson_returns_null_on_failure_or_garbage(string? input)
        => Assert.Null(CapabilityCatalog.ParseJson(input));

    [Fact]
    public void StaticFallback_has_all_12_kinds_in_catalog_order()
    {
        var kinds = CapabilityCatalog.StaticFallback;
        Assert.Equal(12, kinds.Count);

        var ids = kinds.Select(k => k.Id).ToArray();
        Assert.Equal(new[] { "image", "video", "music", "edit", "i2v", "foley", "ltx", "faceid", "pulid", "infinitetalk", "latentsync", "speech" }, ids);
    }

    [Fact]
    public void StaticFallback_ready_kinds_are_the_first_seven()
    {
        var ready = CapabilityCatalog.StaticFallback.Where(k => k.Ready).Select(k => k.Id).ToList();
        Assert.Equal(new[] { "image", "video", "music", "edit", "i2v", "foley", "ltx" }, ready);
    }

    [Fact]
    public void StaticFallback_gated_kinds_have_requires_set()
    {
        var gated = CapabilityCatalog.StaticFallback.Where(k => !k.Ready).ToList();
        Assert.Equal(5, gated.Count);
        Assert.All(gated, k => Assert.False(string.IsNullOrWhiteSpace(k.Requires)));
    }

    [Fact]
    public void StaticFallback_groups_are_only_image_video_audio()
    {
        var groups = CapabilityCatalog.StaticFallback.Select(k => k.Group).Distinct().OrderBy(g => g).ToList();
        Assert.Equal(new[] { "audio", "image", "video" }, groups);
    }
}
