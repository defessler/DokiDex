using System;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// ModelManager's pure verify-decision seam (optional media-catalog sha256, added alongside incremental
// stream hashing in DownloadAsync -- see leaf 2.7 of docs/cc-experience-plan-2026-07.md). Unlike
// LlmModelManager.VerifyDecision this catalog has no exact byte size and no "UNVERIFIED" sentinel: an
// entry with no sha256 simply opts out of verification (the pre-G1, common case today).
public class ModelManagerTests
{
    [Fact]
    public void VerifySha256_returns_null_when_no_hash_is_expected()
    {
        Assert.Null(ModelManager.VerifySha256("f.safetensors", null, "deadbeef"));
        Assert.Null(ModelManager.VerifySha256("f.safetensors", "", "deadbeef"));
        Assert.Null(ModelManager.VerifySha256("f.safetensors", "   ", "deadbeef"));
    }

    [Fact]
    public void VerifySha256_happy_path_returns_null()
        => Assert.Null(ModelManager.VerifySha256("f.safetensors", "abc123", "abc123"));

    [Fact]
    public void VerifySha256_matches_case_insensitively()
        => Assert.Null(ModelManager.VerifySha256("f.safetensors", "ABC123", "abc123"));

    [Fact]
    public void VerifySha256_flags_a_mismatch_naming_the_file_and_both_hashes()
    {
        var err = ModelManager.VerifySha256("f.safetensors", "abc123", "deadbeef");
        Assert.NotNull(err);
        Assert.Contains("f.safetensors", err);
        Assert.Contains("abc123", err);
        Assert.Contains("deadbeef", err);
        Assert.Contains("sha256", err, StringComparison.OrdinalIgnoreCase);
    }
}
