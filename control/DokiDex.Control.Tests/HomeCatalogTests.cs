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
        // "code" (3.1) is a new fourth group alongside the original three; the dark-feature cards (3.4) stay
        // within make/manage.
        Assert.All(HomeCatalog.Capabilities, c => Assert.Contains(c.Group, new[] { "make", "talk", "manage", "code" }));
    }

    [Fact]
    public void Catalog_starters_target_real_views_and_have_labels()
    {
        // "copy" is a pseudo-view (not a Studio section): the SPA's applyStarter special-cases it to copy the
        // starter's Prompt to the clipboard instead of switching views (used by the doki code starter, 3.1).
        // "help" is the in-app docs view added in 3.2 (the doki code card's "open the docs" starter).
        var views = new[] { "create", "director", "chat", "cast", "voice", "flow", "scene", "library", "models", "status", "memory", "copy", "help" }.ToHashSet();
        foreach (var c in HomeCatalog.Capabilities)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Blurb));
            foreach (var s in c.Starters)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Label));
                Assert.Contains(s.View, views);   // every starter jumps to a real Studio view (or the "copy" pseudo-view)
            }
        }
    }

    [Fact]
    public void Code_card_needs_agent_mode_and_llama_swap_not_a_model()
    {
        // F3 correction: readiness is agent-mode-up + llama-swap-healthy, NEVER a specific model presence check.
        var code = HomeCatalog.Capabilities.First(c => c.Id == "code");
        Assert.Equal("agent", code.Requires.Mode);
        Assert.Equal("llama-swap", code.Requires.Service);
        Assert.Null(code.Requires.Model);

        var notReady = HomeCatalog.Annotate(Snap("media")).First(c => c.Capability.Id == "code");
        Assert.Equal("needs-mode", notReady.Readiness.Status);
        var noSwap = HomeCatalog.Annotate(Snap("agent")).First(c => c.Capability.Id == "code");
        Assert.Equal("needs-setup", noSwap.Readiness.Status);
        var ready = HomeCatalog.Annotate(Snap("agent", up: new[] { "llama-swap" })).First(c => c.Capability.Id == "code");
        Assert.Equal("ready", ready.Readiness.Status);
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
        Assert.Empty(none.ModelsPresent);                  // no manager tags passed -> empty, not a crash
    }

    // ---- 2.8: ModelsPresent is the union of media (ModelManager.PresentTags) + LLM (LlmModelManager.PresentTags)
    //      tags, passed in as plain string collections so SnapshotFrom itself stays pure/no-I/O. ----

    [Fact]
    public void SnapshotFrom_unions_media_and_llm_model_tags()
    {
        var snap = HomeCatalog.SnapshotFrom(null, mediaModelsPresent: new[] { "sdxl-base", "image" }, llmModelsPresent: new[] { "coder-fast", "vision" });
        Assert.Equal(new[] { "sdxl-base", "image", "coder-fast", "vision" }.ToHashSet(), snap.ModelsPresent);
    }

    [Fact]
    public void SnapshotFrom_model_tags_are_case_insensitive_and_ignore_blanks()
    {
        var snap = HomeCatalog.SnapshotFrom(null, mediaModelsPresent: new[] { "Image", "" }, llmModelsPresent: new string?[] { "  ", null }!);
        Assert.Contains("image", snap.ModelsPresent);      // case-insensitive set
        Assert.Single(snap.ModelsPresent);                 // blanks/nulls never make it in
    }

    [Fact]
    public void A_model_gated_capability_becomes_ready_once_SnapshotFrom_sees_that_tag()
    {
        // end-to-end: Resolve against a snapshot built the same way GET /api/home builds it.
        var requires = new CapabilityRequires(null, null, "vision");
        var withoutIt = HomeCatalog.Resolve(requires, HomeCatalog.SnapshotFrom(null, llmModelsPresent: new[] { "coder-fast" }));
        Assert.Equal("needs-setup", withoutIt.Status);
        var withIt = HomeCatalog.Resolve(requires, HomeCatalog.SnapshotFrom(null, llmModelsPresent: new[] { "vision" }));
        Assert.Equal("ready", withIt.Status);
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

    // ---- quick-start routing (the Home "just start typing" box): a question -> Chat, else -> Create with an
    //      inferred gen kind. Pure + unit-tested; the SPA calls GET /api/home/route on submit. ----

    [Theory]
    [InlineData("how do I export a video?", "chat", null)]   // ends with ? -> question
    [InlineData("what can this make", "chat", null)]         // starts with a question word
    [InlineData("a neon dragon over a city", "create", "image")]
    [InlineData("a 5 second video of a cat", "create", "video")]
    [InlineData("an upbeat synthwave song", "create", "music")]
    public void RouteQuickStart_routes_questions_to_chat_and_prompts_to_create(string input, string view, string? kind)
    {
        var r = HomeCatalog.RouteQuickStart(input);
        Assert.Equal(view, r.View);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(input, r.Prompt);   // the typed text is always carried into the target view
    }

    [Fact]
    public void RouteQuickStart_defaults_blank_to_create_image_with_no_prompt()
    {
        var r = HomeCatalog.RouteQuickStart("   ");
        Assert.Equal("create", r.View);
        Assert.Equal("image", r.Kind);
        Assert.True(string.IsNullOrEmpty(r.Prompt));
    }

    [Fact]
    public void Every_capability_has_a_non_blank_mini_guide()
    {
        Assert.All(HomeCatalog.Capabilities, c =>
        {
            Assert.True(c.Guide.Count >= 2, $"{c.Id} needs a mini-guide (>= 2 steps)");
            Assert.All(c.Guide, step => Assert.False(string.IsNullOrWhiteSpace(step)));
        });
    }
}
