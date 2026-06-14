using System;
using System.IO;
using DokiCode.Control.Services;
using Xunit;

namespace DokiCode.Control.Tests;

// The updater's swap/rollback/version logic is the hardest-to-reverse code in the app — these pin
// the safety guarantee the review demanded: a bad/incomplete staged file is NEVER promoted, and the
// running exe is never left missing. TryApplyStaged is internal (InternalsVisibleTo in the csproj).
public class UpdaterTests
{
    [Theory]
    [InlineData("v0.2.0", "v0.1.0", true)]
    [InlineData("v0.1.0", "v0.1.0", false)]
    [InlineData("v0.1.0", "v0.2.0", false)]
    [InlineData("v1.0.0-rc1", "v0.9.0", true)]   // pre-release suffix stripped for the compare
    [InlineData("v1.0", "v0.9", true)]           // 2-part
    [InlineData("garbage", "v1.0.0", false)]     // malformed -> false (never "newer")
    [InlineData("v1.2.3", "not-a-version", false)]
    public void IsNewer_handles_v_strip_prerelease_and_malformed(string latest, string running, bool expected)
        => Assert.Equal(expected, Updater.IsNewer(latest, running));

    [Theory]
    [InlineData("DokiCode-v1.0.0-win-x64.exe", "v1.0.0")]
    [InlineData("DokiCode-v1.0.0-rc1-win-x64.exe", "v1.0.0-rc1")]   // keeps the hyphenated pre-release suffix
    [InlineData(@"C:\x\DokiCode-v2.3.4-win-x64.exe", "v2.3.4")]
    public void TagFromAssetFile_roundtrips_including_prerelease(string file, string expectedTag)
        => Assert.Equal(expectedTag, Updater.TagFromAssetFile(file));

    [Theory]
    [InlineData("SomethingElse.exe")]
    [InlineData("DokiCode--win-x64.exe")]   // empty tag between prefix/suffix
    [InlineData("notes.txt")]
    public void TagFromAssetFile_rejects_foreign_names(string file)
        => Assert.Null(Updater.TagFromAssetFile(file));

    [Fact]
    public void TryApplyStaged_swaps_in_and_preserves_old()
    {
        var dir = NewTempDir();
        try
        {
            var current = FakeExe(Path.Combine(dir, "app.exe"), 0x11);
            var staged = FakeExe(Path.Combine(dir, "staged.exe"), 0x22);
            Assert.True(Updater.TryApplyStaged(staged, current));
            Assert.True(File.Exists(current));
            Assert.Equal(0x22, File.ReadAllBytes(current)[100]);   // current now holds the staged bytes
            Assert.True(File.Exists(current + ".old"));            // the previous image is kept for rollback/sweep
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TryApplyStaged_rejects_incomplete_staged_and_leaves_current_intact()
    {
        var dir = NewTempDir();
        try
        {
            var current = FakeExe(Path.Combine(dir, "app.exe"), 0x11);
            var tooSmall = Path.Combine(dir, "staged.exe");
            File.WriteAllBytes(tooSmall, new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 });   // MZ but far under the size floor
            Assert.False(Updater.TryApplyStaged(tooSmall, current));
            Assert.True(File.Exists(current));                      // untouched
            Assert.Equal(0x11, File.ReadAllBytes(current)[100]);    // still the original bytes
            Assert.False(File.Exists(current + ".old"));            // running image never renamed
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TryApplyStaged_missing_staged_returns_false_and_keeps_current()
    {
        var dir = NewTempDir();
        try
        {
            var current = FakeExe(Path.Combine(dir, "app.exe"), 0x11);
            Assert.False(Updater.TryApplyStaged(Path.Combine(dir, "nope.exe"), current));
            Assert.True(File.Exists(current));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CleanUpSuperseded_sweeps_sidecars_but_keeps_the_exe()
    {
        var dir = NewTempDir();
        try
        {
            var exe = FakeExe(Path.Combine(dir, "app.exe"));
            File.WriteAllText(exe + ".old", "x");
            File.WriteAllText(exe + ".new", "x");
            Updater.CleanUpSuperseded(exe);
            Assert.True(File.Exists(exe));
            Assert.False(File.Exists(exe + ".old"));
            Assert.False(File.Exists(exe + ".new"));
        }
        finally { Cleanup(dir); }
    }

    static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "doki-upd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }
    static string FakeExe(string path, byte fill = 0x90)
    {
        var b = new byte[8192];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        for (int i = 2; i < b.Length; i++) b[i] = fill;
        File.WriteAllBytes(path, b);
        return path;
    }
    static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }
}
