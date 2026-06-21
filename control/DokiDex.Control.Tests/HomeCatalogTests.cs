using System.Linq;
using DokiDex.Control.Models;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The guided Home hub's content catalog + the PURE readiness resolver (requires + a live status snapshot -> badge +
// next-step). The logic is server-side so it's unit-tested like the rest of the stack; the SPA Home view just renders
// /api/home. Locks the resolver's precedence + the catalog's structural invariants.
public class HomeCatalogTests
{
    private static HomeStatusSnapshot Snap(string? mode, string[]? up = null, string[]? models = null)
        => new(mode,
               new System.Collections.Generic.HashSet<string>(up ?? System.Array.Empty<string>()),
               new System.Collections.Generic.HashSet<string>(models ?? System.Array.Empty<string>()));

    [Fact]
    public void Resolve_no_requirements_is_ready()
        => Assert.Equal("ready", HomeCatalog.Resolve(new CapabilityRequires(null, null, null), Snap("agent")).Status);

    [Fact]
    public void Resolve_mode_mismatch_needs_a_mode_switch()
    {
        var r = HomeCatalog.Resolve(new CapabilityRequires("media", null, null), Snap("agent"));
        Assert.Equal("needs-mode", r.Status);
        Assert.Equal("mode:media", r.Action);
        Assert.Contains("media", r.NextStep!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_mode_match_is_ready()
        => Assert.Equal("ready", HomeCatalog.Resolve(new CapabilityRequires("media", null, null), Snap("media")).Status);

    [Fact]
    public void Resolve_missing_service_needs_setup()
    {
        var r = HomeCatalog.Resolve(new CapabilityRequires(null, "tts", null), Snap("agent", up: new[] { "llama-swap" }));
        Assert.Equal("needs-setup", r.Status);
        Assert.Equal("service:tts", r.Action);
    }

    [Fact]
    public void Resolve_present_service_is_ready()
        => Assert.Equal("ready", HomeCatalog.Resolve(new CapabilityRequires(null, "tts", null), Snap("agent", up: new[] { "tts" })).Status);

    [Fact]
    public void Resolve_missing_model_needs_setup()
    {
        var r = HomeCatalog.Resolve(new CapabilityRequires(null, null, "vision"), Snap("agent", models: new[] { "coder-fast" }));
        Assert.Equal("needs-setup", r.Status);
        Assert.Equal("model:vision", r.Action);
    }

    [Fact]
    public void Resolve_mode_is_checked_before_service()
    {
        // a service can't run in the wrong GPU group anyway, so a mode mismatch dominates.
        var r = HomeCatalog.Resolve(new CapabilityRequires("media", "media", null), Snap("agent"));
        Assert.Equal("needs-mode", r.Status);
    }

    [Fact]
    public void Catalog_has_all_ten_areas_grouped_make_talk_manage()
    {
        var ids = HomeCatalog.Capabilities.Select(c => c.Id).ToHashSet();
        foreach (var expect in new[] { "create", "director", "chat", "cast", "voice", "flow", "scene", "library", "models", "status" })
            Assert.Contains(expect, ids);
        Assert.All(HomeCatalog.Capabilities, c => Assert.Contains(c.Group, new[] { "make", "talk", "manage" }));
    }

    [Fact]
    public void Catalog_starters_target_real_views_and_have_labels()
    {
        var views = new[] { "create", "director", "chat", "cast", "voice", "flow", "scene", "library", "models", "status" }.ToHashSet();
        foreach (var c in HomeCatalog.Capabilities)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Blurb));
            foreach (var s in c.Starters)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Label));
                Assert.Contains(s.View, views);   // every starter jumps to a real Studio view
            }
        }
    }

    [Fact]
    public void Annotate_pairs_every_capability_with_a_readiness()
    {
        var annotated = HomeCatalog.Annotate(Snap("agent", up: new[] { "llama-swap" }));
        Assert.Equal(HomeCatalog.Capabilities.Count, annotated.Count);
        Assert.All(annotated, a => Assert.False(string.IsNullOrWhiteSpace(a.Readiness.Status)));
    }

    [Fact]
    public void SnapshotFrom_maps_the_llm_group_to_agent_mode_and_healthy_services_to_up()
    {
        var doc = new StatusDoc
        {
            Gpu = new GpuStatus { ActiveGroup = "llm" },
            Services = new()
            {
                new ServiceStatus { Name = "llama-swap", Healthy = true },
                new ServiceStatus { Name = "tts", Healthy = false },
            },
        };
        var snap = HomeCatalog.SnapshotFrom(doc);
        Assert.Equal("agent", snap.Mode);                  // GPU group "llm" -> user term "agent"
        Assert.Contains("llama-swap", snap.ServicesUp);
        Assert.DoesNotContain("tts", snap.ServicesUp);     // unhealthy -> not up
    }

    [Fact]
    public void SnapshotFrom_handles_media_group_and_a_null_doc()
    {
        Assert.Equal("media", HomeCatalog.SnapshotFrom(new StatusDoc { Gpu = new GpuStatus { ActiveGroup = "media" } }).Mode);
        var none = HomeCatalog.SnapshotFrom(null);
        Assert.Equal("none", none.Mode);                   // null doc -> idle
        Assert.Empty(none.ServicesUp);
    }

    [Fact]
    public void Voice_card_needs_tts_until_that_service_is_up()
    {
        // end-to-end through the catalog: Voice requires the tts service.
        var withoutTts = HomeCatalog.Annotate(Snap("agent", up: new[] { "llama-swap" })).First(c => c.Capability.Id == "voice");
        Assert.Equal("needs-setup", withoutTts.Readiness.Status);
        var withTts = HomeCatalog.Annotate(Snap("agent", up: new[] { "llama-swap", "tts" })).First(c => c.Capability.Id == "voice");
        Assert.Equal("ready", withTts.Readiness.Status);
    }
}
