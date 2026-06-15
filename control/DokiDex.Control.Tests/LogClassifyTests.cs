using DokiDex.Control.ViewModels;
using Xunit;

namespace DokiDex.Control.Tests;

// Log severity is colour-coded by CONTENT, not by stream (the bug I fixed: llama-server logs
// to stderr, so treating stderr as 'error' painted everything red).
public class LogClassifyTests
{
    [Theory]
    [InlineData("Exception in handler", "error")]
    [InlineData("model load failed", "error")]
    [InlineData("Traceback (most recent call last)", "error")]
    [InlineData("[WARN] vae tiling fallback", "warn")]
    [InlineData("coder-big ready on :8080", "good")]
    [InlineData("swap: loading coder-big", "good")]
    [InlineData("POST /v1/chat/completions 200 47 tok", "good")]
    [InlineData("kv 0: general.architecture = qwen3", "info")]
    public void Classifies_by_content(string line, string expected)
    {
        Assert.Equal(expected, LogsViewModel.Classify(line, false));
    }

    [Fact]
    public void Stderr_normal_line_is_not_forced_to_error()
    {
        // a benign stderr line must NOT be classed error just because it came from stderr
        Assert.NotEqual("error", LogsViewModel.Classify("llama_model_loader: - kv 1: general.name", true));
    }
}
