using System;
using System.Collections.Generic;
using System.IO;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// LlmModelManager's pure seams (catalog parse, status decision, verify decision) plus the disk-touching but
// network-free surface (List/Install/Delete against a fixture catalog + scratch "models root"). No network.
public class LlmModelManagerTests
{
    private const string TwoEntryCatalogJson = """
    {
      "models": [
        {
          "id": "coder-fast",
          "role": "coder-fast",
          "label": "Qwen3-Coder-30B-A3B-Instruct (UD-Q4_K_XL)",
          "files": [
            { "file": "models/coder-fast.gguf", "url": "https://example.test/coder-fast.gguf", "sha256": "aa11", "size": 100 }
          ],
          "sizeGb": 17.67,
          "llamaSwapModel": "coder-fast",
          "notes": "Daily-driver coder tier."
        },
        {
          "id": "coder-big",
          "role": "coder-big",
          "label": "gpt-oss-120b (MXFP4)",
          "files": [
            { "file": "models/coder-big-1.gguf", "url": "https://example.test/coder-big-1.gguf", "sha256": "bb11", "size": 10 },
            { "file": "models/coder-big-2.gguf", "url": "https://example.test/coder-big-2.gguf", "sha256": "bb22", "size": 20 },
            { "file": "models/coder-big-3.gguf", "url": "https://example.test/coder-big-3.gguf", "sha256": "bb33", "size": 30 }
          ],
          "sizeGb": 63.39,
          "llamaSwapModel": "coder-big",
          "notes": "3-part MoE."
        }
      ]
    }
    """;

    // ---------------------------------------------------------------------------------------------------
    // Catalog parse (pure).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ParseCatalog_reads_a_single_file_entry()
    {
        var entries = LlmModelManager.ParseCatalog(TwoEntryCatalogJson);
        var coderFast = Assert.Single(entries, e => e.Id == "coder-fast");
        Assert.Equal("coder-fast", coderFast.Role);
        Assert.Equal("Qwen3-Coder-30B-A3B-Instruct (UD-Q4_K_XL)", coderFast.Label);
        Assert.Equal(17.67, coderFast.SizeGb);
        Assert.Equal("coder-fast", coderFast.LlamaSwapModel);
        var file = Assert.Single(coderFast.Files);
        Assert.Equal("models/coder-fast.gguf", file.File);
        Assert.Equal("https://example.test/coder-fast.gguf", file.Url);
        Assert.Equal("aa11", file.Sha256);
        Assert.Equal(100, file.Size);
    }

    [Fact]
    public void ParseCatalog_reads_a_multi_part_entry_in_order()
    {
        var entries = LlmModelManager.ParseCatalog(TwoEntryCatalogJson);
        var coderBig = Assert.Single(entries, e => e.Id == "coder-big");
        Assert.Equal(3, coderBig.Files.Count);
        Assert.Equal(new[] { "models/coder-big-1.gguf", "models/coder-big-2.gguf", "models/coder-big-3.gguf" },
            coderBig.Files.ConvertAll(f => f.File));
        Assert.Equal(new long[] { 10, 20, 30 }, coderBig.Files.ConvertAll(f => f.Size));
    }

    [Fact]
    public void ParseCatalog_handles_a_null_llamaSwapModel()
    {
        const string json = """
        { "models": [ { "id": "fim", "role": "fim", "label": "FIM", "files": [], "sizeGb": 3.29, "llamaSwapModel": null, "notes": "n" } ] }
        """;
        var entries = LlmModelManager.ParseCatalog(json);
        Assert.Null(Assert.Single(entries).LlamaSwapModel);
    }

    [Fact]
    public void ParseCatalog_of_an_empty_models_array_returns_an_empty_list()
        => Assert.Empty(LlmModelManager.ParseCatalog("""{ "models": [] }"""));

    // ---------------------------------------------------------------------------------------------------
    // Status decision (pure).
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new bool[] { }, "missing")]
    [InlineData(new[] { true }, "present")]
    [InlineData(new[] { false }, "missing")]
    [InlineData(new[] { true, true, true }, "present")]
    [InlineData(new[] { true, false, true }, "partial")]
    [InlineData(new[] { false, false }, "missing")]
    public void StatusFromPresence_classifies_present_partial_missing(bool[] present, string expected)
        => Assert.Equal(expected, LlmModelManager.StatusFromPresence(present));

    // ---------------------------------------------------------------------------------------------------
    // Verify decision (pure): size mismatch, sha mismatch, UNVERIFIED refusal, happy path.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void VerifyDecision_happy_path_returns_null()
        => Assert.Null(LlmModelManager.VerifyDecision("f.gguf", "abc123", "ABC123", 100, 100));

    [Fact]
    public void VerifyDecision_flags_a_size_mismatch_before_checking_sha()
    {
        var err = LlmModelManager.VerifyDecision("f.gguf", "abc123", "abc123", 100, 99);
        Assert.Contains("f.gguf", err);
        Assert.Contains("99", err);
        Assert.Contains("100", err);
    }

    [Fact]
    public void VerifyDecision_flags_a_sha_mismatch_case_insensitively_on_match()
    {
        Assert.Null(LlmModelManager.VerifyDecision("f.gguf", "ABC123", "abc123", 100, 100));   // case differs, still ok
        var err = LlmModelManager.VerifyDecision("f.gguf", "abc123", "deadbeef", 100, 100);
        Assert.Contains("f.gguf", err);
        Assert.Contains("sha256", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyDecision_refuses_an_UNVERIFIED_catalog_entry_regardless_of_actuals()
    {
        var err = LlmModelManager.VerifyDecision("f.gguf", "UNVERIFIED", "anything", 100, 100);   // even a byte-perfect match
        Assert.NotNull(err);
        Assert.Contains("G1", err);
        var errCi = LlmModelManager.VerifyDecision("f.gguf", "unverified", "x", 1, 2);   // case-insensitive marker too
        Assert.NotNull(errCi);
    }

    // ---------------------------------------------------------------------------------------------------
    // Disk-space decision (pure).
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 99, false)]
    [InlineData(0, 0, true)]
    public void FitsFreeSpace_compares_needed_to_available(long needed, long available, bool expected)
        => Assert.Equal(expected, LlmModelManager.FitsFreeSpace(needed, available));

    // ---------------------------------------------------------------------------------------------------
    // Instance surface against a fixture catalog + scratch models root (no network; files pre-seeded on disk).
    // ---------------------------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        public readonly string Dir;
        public readonly string CatalogPath;
        public readonly string ModelsRoot;
        public readonly LlmModelManager Manager;

        public Fixture(string json)
        {
            Dir = Path.Combine(Path.GetTempPath(), "dokidex-llmcat-" + Guid.NewGuid().ToString("N"));
            ModelsRoot = Path.Combine(Dir, "root");
            Directory.CreateDirectory(ModelsRoot);
            CatalogPath = Path.Combine(Dir, "llm-model-catalog.json");
            File.WriteAllText(CatalogPath, json);
            Manager = new LlmModelManager(CatalogPath, ModelsRoot);
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void List_reports_missing_when_no_files_are_on_disk()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        var result = fx.Manager.List();
        Assert.NotEmpty(result.Models);
        Assert.All(result.Models, m => Assert.Equal("missing", m.Status));
    }

    [Fact]
    public void List_reports_partial_when_some_but_not_all_parts_are_present()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        Directory.CreateDirectory(Path.Combine(fx.ModelsRoot, "models"));
        // coder-big has 3 parts (sizes 10/20/30); seed only part 1 at the exact expected size.
        File.WriteAllBytes(Path.Combine(fx.ModelsRoot, "models", "coder-big-1.gguf"), new byte[10]);

        var result = fx.Manager.List();
        var coderBig = result.Models.Find(m => m.Id == "coder-big");
        Assert.Equal("partial", coderBig!.Status);
        Assert.True(coderBig.Files.Find(f => f.File == "models/coder-big-1.gguf")!.Present);
        Assert.False(coderBig.Files.Find(f => f.File == "models/coder-big-2.gguf")!.Present);
    }

    [Fact]
    public void List_reports_present_when_all_parts_are_on_disk()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        Directory.CreateDirectory(Path.Combine(fx.ModelsRoot, "models"));
        File.WriteAllBytes(Path.Combine(fx.ModelsRoot, "models", "coder-fast.gguf"), new byte[100]);

        var result = fx.Manager.List();
        var coderFast = result.Models.Find(m => m.Id == "coder-fast");
        Assert.Equal("present", coderFast!.Status);
    }

    [Fact]
    public void List_surfaces_a_message_when_the_catalog_file_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dokidex-llmcat-nofile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mgr = new LlmModelManager(Path.Combine(dir, "does-not-exist.json"), dir);
            var result = mgr.List();
            Assert.Empty(result.Models);
            Assert.NotNull(result.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Install_returns_unknown_for_an_id_not_in_the_catalog()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        Assert.Equal("unknown", fx.Manager.Install("does-not-exist"));
    }

    [Fact]
    public void Install_returns_installed_when_every_part_is_already_present_at_exact_size()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        Directory.CreateDirectory(Path.Combine(fx.ModelsRoot, "models"));
        File.WriteAllBytes(Path.Combine(fx.ModelsRoot, "models", "coder-fast.gguf"), new byte[100]);
        Assert.Equal("installed", fx.Manager.Install("coder-fast"));
    }

    [Fact]
    public void Delete_returns_false_for_an_unknown_id_and_true_for_a_known_one()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        Assert.False(fx.Manager.Delete("does-not-exist"));

        Directory.CreateDirectory(Path.Combine(fx.ModelsRoot, "models"));
        var p = Path.Combine(fx.ModelsRoot, "models", "coder-fast.gguf");
        File.WriteAllBytes(p, new byte[100]);
        Assert.True(fx.Manager.Delete("coder-fast"));
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void PresentTags_includes_id_and_role_only_for_fully_present_entries()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        Directory.CreateDirectory(Path.Combine(fx.ModelsRoot, "models"));
        File.WriteAllBytes(Path.Combine(fx.ModelsRoot, "models", "coder-fast.gguf"), new byte[100]);   // "present"
        File.WriteAllBytes(Path.Combine(fx.ModelsRoot, "models", "coder-big-1.gguf"), new byte[10]);   // coder-big stays "partial"

        var tags = fx.Manager.PresentTags();
        Assert.Contains("coder-fast", tags);   // id
        // coder-fast's role also happens to be "coder-fast" in the fixture, so this doubles as the role check.
        Assert.DoesNotContain("coder-big", tags);   // partial entries never contribute a tag
    }

    [Fact]
    public void Delete_never_touches_files_outside_the_entrys_own_files_list()
    {
        using var fx = new Fixture(TwoEntryCatalogJson);
        Directory.CreateDirectory(Path.Combine(fx.ModelsRoot, "models"));
        var untouched = Path.Combine(fx.ModelsRoot, "models", "coder-big-1.gguf");
        File.WriteAllBytes(untouched, new byte[10]);   // belongs to coder-big, not coder-fast

        fx.Manager.Delete("coder-fast");   // coder-fast's own file was never created
        Assert.True(File.Exists(untouched));
    }
}
