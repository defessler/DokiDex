using System;
using System.IO;
using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

// The repo-independent home resolution: a saved InstallRoot wins, else walk up to doki.ps1 (dev), else
// fall back to the launched-exe dir — never a hardcoded path to the old location.
public class RepoPathsTests
{
    private static string TempDirWithDoki()
    {
        var d = Path.Combine(Path.GetTempPath(), "dokidex-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "doki.ps1"), "# test");
        return d;
    }

    [Fact]
    public void ResolveRoot_prefers_a_valid_configured_install_root()
    {
        var home = TempDirWithDoki();
        try { Assert.Equal(home, RepoPaths.ResolveRoot(home, Path.GetTempPath())); }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void ResolveRoot_ignores_a_configured_root_without_doki_and_walks_up()
    {
        var home = TempDirWithDoki();
        var child = Path.Combine(home, "a", "b");
        Directory.CreateDirectory(child);
        try { Assert.Equal(home, RepoPaths.ResolveRoot(@"C:\definitely-no-doki-here-xyz", child)); }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void ResolveRoot_falls_back_to_the_first_start_when_nothing_found()
    {
        var start = Path.Combine(Path.GetTempPath(), "dokidex-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(start);
        try { Assert.Equal(start, RepoPaths.ResolveRoot(null, start)); }
        finally { Directory.Delete(start, true); }
    }

    [Fact]
    public void IsValidHome_requires_doki_ps1()
    {
        var home = TempDirWithDoki();
        try
        {
            Assert.True(InstallLocator.IsValidHome(home));
            Assert.False(InstallLocator.IsValidHome(Path.GetTempPath()));
            Assert.False(InstallLocator.IsValidHome(null));
            Assert.False(InstallLocator.IsValidHome(""));
        }
        finally { Directory.Delete(home, true); }
    }
}
