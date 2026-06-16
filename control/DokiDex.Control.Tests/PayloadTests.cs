using System;
using System.IO;
using System.IO.Compression;
using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

// The embedded runtime payload: it must be present in the built app assembly, extract with subdirs, refresh
// scripts only when asked, never delete non-payload data (models), and resist zip-slip.
public class PayloadTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dokidex-payload-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static MemoryStream MakeZip(params (string name, string content)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = z.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Bundled_payload_is_embedded_in_the_app_assembly()
        => Assert.True(Payload.Available, "StagePayload (make-payload.ps1) must embed DokiDex.payload.zip into the app");

    [Fact]
    public void ExtractZip_writes_entries_with_subdirs()
    {
        var dest = TempDir();
        try
        {
            using var zip = MakeZip(("doki.ps1", "# doki"), ("serving/start-serving.ps1", "# start"));
            Payload.ExtractZip(zip, dest, overwriteExisting: true);
            Assert.Equal("# doki", File.ReadAllText(Path.Combine(dest, "doki.ps1")));
            Assert.Equal("# start", File.ReadAllText(Path.Combine(dest, "serving", "start-serving.ps1")));
        }
        finally { Directory.Delete(dest, true); }
    }

    [Fact]
    public void ExtractZip_preserves_existing_unless_overwriting_and_never_touches_non_payload()
    {
        var dest = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "models"));
            File.WriteAllText(Path.Combine(dest, "models", "big.gguf"), "WEIGHTS");   // a heavy non-payload asset
            File.WriteAllText(Path.Combine(dest, "doki.ps1"), "OLD");

            using var zip = MakeZip(("doki.ps1", "NEW"));
            Payload.ExtractZip(zip, dest, overwriteExisting: false);
            Assert.Equal("OLD", File.ReadAllText(Path.Combine(dest, "doki.ps1")));                 // preserved
            Assert.Equal("WEIGHTS", File.ReadAllText(Path.Combine(dest, "models", "big.gguf")));   // untouched

            zip.Position = 0;
            Payload.ExtractZip(zip, dest, overwriteExisting: true);
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(dest, "doki.ps1")));                 // refreshed
            Assert.Equal("WEIGHTS", File.ReadAllText(Path.Combine(dest, "models", "big.gguf")));   // still untouched
        }
        finally { Directory.Delete(dest, true); }
    }

    [Fact]
    public void ExtractZip_rejects_zip_slip_paths()
    {
        var dest = TempDir();
        try
        {
            using var zip = MakeZip(("../escape.txt", "PWNED"), ("ok.txt", "fine"));
            Payload.ExtractZip(zip, dest, overwriteExisting: true);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dest)!, "escape.txt")));
            Assert.True(File.Exists(Path.Combine(dest, "ok.txt")));
        }
        finally { Directory.Delete(dest, true); }
    }
}
