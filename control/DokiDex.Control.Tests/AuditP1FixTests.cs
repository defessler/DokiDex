using DokiDex.Control.Services;
using DokiDex.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DokiDex.Control.Tests;

// Tests for the 2026-06 codebase-audit P1 fixes (security + correctness). See docs/audit-2026-06.md.

// P1-3 — GenRequest gained Audio/Engine so InfiniteTalk/LatentSync/Speak work from the panel.
public class GenArgsAudioEngineTests
{
    [Theory]
    [InlineData("infinitetalk")]
    [InlineData("latentsync")]
    [InlineData("speech")]
    public void Audio_emitted_for_audio_driven_kinds(string kind)
    {
        var a = GenCli.BuildArgs(new GenRequest("x", kind, InitImage: "p.png", Audio: @"C:\v\s.wav", OutPath: "o"));
        var i = a.IndexOf("-Audio");
        Assert.True(i >= 0, "-Audio should be emitted for " + kind);
        Assert.Equal(@"C:\v\s.wav", a[i + 1]);
    }

    [Fact]
    public void Audio_not_emitted_for_non_audio_kinds()
        => Assert.DoesNotContain("-Audio", GenCli.BuildArgs(new GenRequest("x", "image", Audio: "s.wav", OutPath: "o")));

    [Fact]
    public void Engine_emitted_only_for_speech()
    {
        var sp = GenCli.BuildArgs(new GenRequest("x", "speech", Engine: "Higgs", OutPath: "o"));
        var i = sp.IndexOf("-Engine");
        Assert.True(i >= 0);
        Assert.Equal("Higgs", sp[i + 1]);
        Assert.DoesNotContain("-Engine", GenCli.BuildArgs(new GenRequest("x", "infinitetalk", InitImage: "p.png", Audio: "a.wav", Engine: "Higgs", OutPath: "o")));
    }
}

// P1-2 — edit_image source paths are scope-validated before reaching doki-gen.ps1 -InitImage.
public class GallerySafePathTests
{
    [Theory]
    [InlineData("koi.png")]
    [InlineData("sub/koi.png")]
    public void Accepts_relative_media_names(string p) => Assert.True(ChatTools.IsGallerySafePath(p));

    [Theory]
    [InlineData("../../passwords.txt")]
    [InlineData("../secret.png")]
    [InlineData("a/../../b.png")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("notes.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_traversal_absolute_and_nonmedia(string? p) => Assert.False(ChatTools.IsGallerySafePath(p));
}

// P1-6 — SwarmGen.TryHandle (drives every generation) is now internal + covered.
public class SwarmGenTryHandleTests
{
    private static bool Handle(string frame, out string? artifact, out string? error, out double lastPct)
    {
        string? art = null, err = null; double pct = -1;
        var terminal = SwarmGen.TryHandle(frame, p => pct = p.Overall, ref art, ref err);
        artifact = art; error = err; lastPct = pct;
        return terminal;
    }

    [Fact]
    public void Keepalive_or_junk_is_non_terminal()
    {
        Assert.False(Handle("not json", out _, out _, out _));
        Assert.False(Handle("{}", out var a, out var e, out _));
        Assert.Null(a);
        Assert.Null(e);
    }

    [Fact]
    public void Gen_progress_reports_overall_and_is_non_terminal()
    {
        var terminal = Handle("{\"gen_progress\":{\"overall_percent\":0.5,\"preview\":\"data:abc\"}}", out _, out _, out var pct);
        Assert.False(terminal);
        Assert.Equal(0.5, pct, 3);
    }

    [Fact]
    public void Error_frame_is_terminal_and_sets_error()
    {
        Assert.True(Handle("{\"error\":\"boom\"}", out _, out var e, out _));
        Assert.Contains("boom", e);
    }

    [Fact]
    public void Single_image_and_images_array_extract_artifact()
    {
        Assert.True(Handle("{\"image\":\"View/x.png\"}", out var a1, out _, out _));
        Assert.Equal("View/x.png", a1);
        Assert.True(Handle("{\"images\":[\"View/y.png\",\"View/z.png\"]}", out var a2, out _, out _));
        Assert.Equal("View/y.png", a2);
    }
}

// P1-1 / P1-7 — the CSRF guard now denies no-Origin (and foreign-Origin) state-changing requests.
public class LocalSecurityMiddlewareTests
{
    private static async Task<(int status, bool nextCalled)> Run(string method, string host, string? origin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Host = new HostString(host);
        if (origin != null) ctx.Request.Headers["Origin"] = origin;
        var called = false;
        var mw = new LocalSecurityMiddleware(_ => { called = true; return Task.CompletedTask; });
        await mw.InvokeAsync(ctx);
        return (ctx.Response.StatusCode, called);
    }

    [Fact]
    public async Task Get_with_no_origin_passes()
    {
        var (status, called) = await Run("GET", "127.0.0.1", null);
        Assert.True(called);
        Assert.NotEqual(403, status);
    }

    [Fact]
    public async Task Forbidden_host_is_denied()
    {
        var (status, called) = await Run("GET", "evil.example.com", null);
        Assert.Equal(403, status);
        Assert.False(called);
    }

    [Fact]
    public async Task Post_with_allowed_origin_passes()
    {
        var (status, called) = await Run("POST", "127.0.0.1", "http://127.0.0.1:5111");
        Assert.True(called);
        Assert.NotEqual(403, status);
    }

    [Fact]
    public async Task Post_with_foreign_origin_is_denied()
    {
        var (status, called) = await Run("POST", "127.0.0.1", "http://evil.example.com");
        Assert.Equal(403, status);
        Assert.False(called);
    }

    [Fact]
    public async Task Post_with_no_origin_is_denied()   // the P1-1 CSRF fix
    {
        var (status, called) = await Run("POST", "127.0.0.1", null);
        Assert.Equal(403, status);
        Assert.False(called);
    }
}
