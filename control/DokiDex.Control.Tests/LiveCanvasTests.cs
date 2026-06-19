using System.Linq;
using DokiDex.Control.Services;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Real-time canvas (slice 1) reuse contract. The live sketch loop is a CLIENT feature over the EXISTING
// /api/generate endpoint: a debounced, single-flight, low-denoise img2img off the sketch canvas bitmap.
// There is NO new server RECIPE/pipeline code — the loop maps onto the already-tested GenRequest/GenCli +
// doki-gen recipe contract (canvas data-URL -> InitImage, -Fast = Z-Image-Turbo 8-step, Strength = the
// initimagecreativity denoise dial). The ONE host-side addition is the Ephemeral persistence flag (no Library
// sidecar, %TEMP% artifact, hidden from Recent()) — a JOB-persistence concern that must never touch the argv.
// These tests LOCK both seams so a future change can't silently break the reuse the live canvas depends on
// (init image + low denoise + the fast recipe) NOR start leaking live frames into the Library / Results grid.
public class LiveCanvasTests
{
    // The exact request the client's renderLive() shapes: a sketch -> fast, low-denoise, seed-locked img2img.
    // Aspect matches the 384x256 (3:2) sketch canvas + the 3/2 .livepane so the drawing isn't squashed.
    private static GenRequest LiveRequest() => new GenRequest(
        "neon koi", "image",
        Fast: true,                 // Z-Image-Turbo 8-step recipe (the low-step real-time base)
        InitImage: @"C:\tmp\sketch.png",  // canvas.toDataURL() decoded server-side (SaveDataUrl) to a path
        Strength: 0.35,             // low denoise -> faithful to the sketch + few effective steps
        Aspect: "3:2",              // MATCHES the 384x256 sketch canvas + the 3/2 live pane (no squash/letterbox)
        Seed: 42,                   // seed-locked so successive renders are coherent (the "live" feel)
        OutPath: @"C:\tmp\out.png");

    [Fact]
    public void Live_canvas_request_emits_fast_init_and_strength_for_low_denoise_img2img()
    {
        // The whole real-time path is just these fields on the existing recipe contract — assert each reaches
        // the argv so the doki-gen body emits initimage + initimagecreativity (Build-GenBody) for img2img.
        var a = GenCli.BuildArgs(LiveRequest());

        Assert.Contains("-Fast", a);

        var ii = a.IndexOf("-InitImage");
        Assert.True(ii >= 0);
        Assert.Equal(@"C:\tmp\sketch.png", a[ii + 1]);

        var si = a.IndexOf("-Strength");
        Assert.True(si >= 0);
        Assert.Equal("0.35", a[si + 1]);   // invariant culture (no comma) -> reaches initimagecreativity verbatim

        var ai = a.IndexOf("-Aspect");
        Assert.True(ai >= 0);
        Assert.Equal("3:2", a[ai + 1]);    // canvas-matched aspect so the live render isn't distorted to a square

        var se = a.IndexOf("-Seed");
        Assert.True(se >= 0);
        Assert.Equal("42", a[se + 1]);     // seed-lock for coherent successive renders
    }

    [Fact]
    public void Live_canvas_stays_a_plain_image_kind_no_doomed_modifiers()
    {
        // The sketch loop is the plain image kind (Z-Image-Turbo img2img), NOT edit/i2v and NOT a post-pass:
        // it must never emit a kind switch or an -Upscale/-Refine that would slow the loop or be a doomed cmd.
        var a = GenCli.BuildArgs(LiveRequest());
        Assert.DoesNotContain("-Video", a);
        Assert.DoesNotContain("-Edit", a);
        Assert.DoesNotContain("-Upscale", a);
        Assert.DoesNotContain("-Refine", a);
        Assert.DoesNotContain("-Reference", a);   // init image drives img2img, not an IP-Adapter reference here
    }

    [Fact]
    public void Ephemeral_is_a_persistence_only_flag_and_never_reaches_the_argv()
    {
        // The live canvas marks its renders Ephemeral (no Library sidecar, %TEMP% artifact, hidden from the
        // Results grid). That flag is purely a host-side persistence decision: it must NOT change the doki-gen
        // argv, so a normal fast img2img and the live-canvas render shell the EXACT same command/recipe.
        var normal = GenCli.BuildArgs(LiveRequest());
        var live   = GenCli.BuildArgs(LiveRequest() with { Ephemeral = true });
        Assert.Equal(normal, live);
    }

    [Fact]
    public void Strength_minus_one_means_recipe_default_not_a_strength_arg()
    {
        // Strength defaults to -1 (recipe default). The denoise dial is only emitted when the client sets it,
        // so a live render without a denoise-slider value still works (Build-GenBody falls back to 0).
        var a = GenCli.BuildArgs(new GenRequest("neon koi", "image", Fast: true, InitImage: "s.png", OutPath: "o"));
        Assert.DoesNotContain("-Strength", a);
        Assert.Contains("-Fast", a);
        Assert.Contains("-InitImage", a);
    }

    // ---- Ephemeral PERSISTENCE decision (FIX 1) ----------------------------------------------------------
    // GenerationJobs needs a live DokiService + SignalR hub + GPU to run, so the persistence behaviour isn't
    // unit-isolable end-to-end. The two decisions the live path depends on — "does this job appear in Recent()?"
    // and "does this job get a Library sidecar?" — are extracted into pure GenJob predicates that both Recent()
    // and RunAsync delegate to. Testing the predicates locks the ephemeral contract GPU-free.

    [Fact]
    public void Ephemeral_job_is_excluded_from_the_Recent_feed()
    {
        // (i) An ephemeral (live-canvas) job must NOT surface in Recent(60) — otherwise the 1.5s pollJobs() grid
        // timer would spray a Results card per debounced stroke-batch render. A normal job still appears.
        Assert.False(GenJob.ShouldAppearInRecent(ephemeral: true));
        Assert.True(GenJob.ShouldAppearInRecent(ephemeral: false));
    }

    [Fact]
    public void Ephemeral_job_does_not_persist_a_Library_sidecar()
    {
        // (ii) An ephemeral job must NOT write a GalleryService sidecar (that would make every stroke-batch render
        // a permanent Library item). A normal job still persists for the Library exactly as before.
        Assert.False(GenJob.ShouldPersist(ephemeral: true));
        Assert.True(GenJob.ShouldPersist(ephemeral: false));
    }

    [Fact]
    public void Recent_excludes_ephemeral_jobs_via_the_persistence_predicate()
    {
        // The instance Recent() filter is wired to the SAME predicate, so the two can't drift: a hand-built
        // ephemeral job is filtered out while a normal one with a higher id stays. (No GPU: jobs are POCOs here.)
        var jobs = new[]
        {
            new GenJob { Id = "g0001", Prompt = "p", Kind = "image", Ephemeral = false },
            new GenJob { Id = "g0002", Prompt = "p", Kind = "image", Ephemeral = true  },
        };
        var recent = GenJob.FilterRecent(jobs).ToList();
        Assert.Single(recent);
        Assert.Equal("g0001", recent[0].Id);
    }
}
