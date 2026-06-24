using DokiDex.Control.Services;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Cross-file sync test: GenArgs.Kinds, GenCli.KindSwitch, GenRequest.OutExtensionFor, and
// CapabilityCatalog.StaticFallback must stay in sync. Pure, no-pwsh — compares static C# data only.
// Adding a new kind to any one of these without updating the others will break this test.
public class KindSyncTests
{
    // All 12 kind ids from the contract
    private static readonly string[] AllKinds =
        { "image", "video", "music", "edit", "i2v", "foley", "ltx", "faceid", "pulid", "infinitetalk", "latentsync", "speech" };

    [Fact]
    public void GenArgs_Kinds_contains_all_12_contract_ids_in_order()
        => Assert.Equal(AllKinds, GenRequest.Kinds);

    [Fact]
    public void Every_GenArgs_Kind_has_an_OutExtensionFor_result()
    {
        foreach (var kind in GenRequest.Kinds)
        {
            var ext = GenRequest.OutExtensionFor(kind);
            Assert.False(string.IsNullOrWhiteSpace(ext), $"OutExtensionFor(\"{kind}\") returned blank");
            Assert.StartsWith(".", ext, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_GenArgs_Kind_except_image_has_a_KindSwitch_entry_and_image_has_none()
    {
        // image is the default kind (no switch); every other kind must map to a -Switch in BuildArgs
        foreach (var kind in GenRequest.Kinds)
        {
            var args = GenCli.BuildArgs(new GenRequest("x", kind, OutPath: "o"));
            if (kind == "image")
            {
                // "image" must NOT emit any kind-specific switch
                Assert.DoesNotContain("-Video", args);
                Assert.DoesNotContain("-Music", args);
                Assert.DoesNotContain("-Edit", args);
                Assert.DoesNotContain("-I2v", args);
                Assert.DoesNotContain("-Foley", args);
                Assert.DoesNotContain("-Ltx", args);
                Assert.DoesNotContain("-FaceId", args);
                Assert.DoesNotContain("-Pulid", args);
                Assert.DoesNotContain("-InfiniteTalk", args);
                Assert.DoesNotContain("-LatentSync", args);
                Assert.DoesNotContain("-Speak", args);
            }
            else
            {
                // Every other kind must emit exactly one kind switch
                var kindSwitches = args.Count(a => a is "-Video" or "-Music" or "-Edit" or "-I2v" or "-Foley"
                    or "-Ltx" or "-FaceId" or "-Pulid" or "-InfiniteTalk" or "-LatentSync" or "-Speak");
                Assert.Equal(1, kindSwitches);
            }
        }
    }

    [Fact]
    public void CapabilityCatalog_StaticFallback_contains_all_12_kinds_in_same_order()
    {
        var fallbackIds = CapabilityCatalog.StaticFallback.Select(k => k.Id).ToArray();
        Assert.Equal(GenRequest.Kinds, fallbackIds);
    }

    [Fact]
    public void OutExtensionFor_matches_expected_per_kind()
    {
        // Spot-check the new kinds introduced in the expansion
        Assert.Equal(".mp4", GenRequest.OutExtensionFor("ltx"));
        Assert.Equal(".mp4", GenRequest.OutExtensionFor("infinitetalk"));
        Assert.Equal(".mp4", GenRequest.OutExtensionFor("latentsync"));
        Assert.Equal(".mp3", GenRequest.OutExtensionFor("speech"));
        Assert.Equal(".png", GenRequest.OutExtensionFor("faceid"));
        Assert.Equal(".png", GenRequest.OutExtensionFor("pulid"));
    }
}
