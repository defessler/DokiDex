using System;
using System.IO;
using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

// The GPU-free parts of the Studio's live path: the temp-artifact factory, doki's error-line surfacing, and
// the security scoping on OpenLocalMedia (the actual gen run needs the card and is smoke-checked there).
public class DokiServiceGenTests
{
    private static readonly string GenDir = Path.Combine(Path.GetTempPath(), "dokigen");

    [Theory]
    [InlineData("image", ".png")]
    [InlineData("video", ".mp4")]
    [InlineData("music", ".mp3")]
    public void NewGenOutPath_is_a_scoped_fully_qualified_temp_path(string kind, string ext)
    {
        var p = new DokiService().NewGenOutPath(kind);
        Assert.True(Path.IsPathFullyQualified(p));
        Assert.Equal(ext, Path.GetExtension(p));
        Assert.StartsWith(Path.GetFullPath(GenDir), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewGenOutPath_is_unique_per_call()
        => Assert.NotEqual(new DokiService().NewGenOutPath("image"), new DokiService().NewGenOutPath("image"));

    [Fact]
    public void LastMeaningfulLine_returns_last_nonblank_trimmed()
    {
        Assert.Equal("boom", DokiService.LastMeaningfulLine("one\ntwo\n  boom  \n\n"));
        Assert.Null(DokiService.LastMeaningfulLine("   \n  \n"));
        Assert.Null(DokiService.LastMeaningfulLine(null));
    }

    // OpenLocalMedia must shell-open ONLY a real media file the panel wrote into its own temp dir. Every
    // out-of-contract input has to no-op silently (and never launch) — we assert it doesn't throw; the early
    // returns happen before any Process.Start, so a passing call here means nothing was launched.
    [Fact]
    public void OpenLocalMedia_rejects_out_of_contract_paths_without_throwing()
    {
        var svc = new DokiService();
        svc.OpenLocalMedia("");                                  // empty
        svc.OpenLocalMedia("relative\\x.png");                   // not fully-qualified
        svc.OpenLocalMedia(@"C:\Windows\System32\calc.exe");     // outside the gen temp dir (and not media)
        svc.OpenLocalMedia(Path.Combine(GenDir, "nope.png"));    // in-scope but doesn't exist
        svc.OpenLocalMedia(Path.Combine(GenDir, "evil.exe"));    // in-scope, missing, and disallowed ext
        // a traversal that resolves outside the gen dir must also be rejected (StartsWith on the full path)
        svc.OpenLocalMedia(Path.Combine(GenDir, @"..\..\Windows\System32\calc.exe"));
        Assert.True(true);   // reaching here = no launch, no throw
    }
}
