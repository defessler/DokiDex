using System;
using System.IO;
using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

public class PrereqsTests
{
    [Fact]
    public void OnPath_finds_an_exe_in_a_listed_dir_and_rejects_misses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dokidex-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "toolx.exe"), "");
            var path = dir + Path.PathSeparator + @"C:\definitely-nonexistent-xyz";
            Assert.True(Prereqs.OnPath("toolx", path));     // resolves toolx -> toolx.exe in dir
            Assert.False(Prereqs.OnPath("nope", path));     // not present
            Assert.False(Prereqs.OnPath("toolx", null));    // no PATH
            Assert.False(Prereqs.OnPath("toolx", ""));
        }
        finally { Directory.Delete(dir, true); }
    }
}
