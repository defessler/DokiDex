using DokiCode.Control.Services;
using Xunit;

namespace DokiCode.Control.Tests;

// The panel's entire data flow depends on parsing `doki status json` correctly (camelCase
// keys, nulls, the gpu object, the profiles map). These pin that contract.
public class StatusParsingTests
{
    private const string Sample = """
    {
      "services": [
        { "name": "llama-swap", "group": "llm", "desc": "agent inference :8080", "port": 8080,
          "ui": "http://127.0.0.1:8080/ui", "vramGB": 26, "healthy": true, "running": true, "pid": 12496,
          "installed": true, "model": "coder-fast", "modelState": "ready",
          "configuredModels": ["coder-big","coder-fast","coder-fast-lite"], "version": "", "update": "", "profiles": ["agent"] },
        { "name": "stt", "group": "llm", "desc": "speech-to-text :8005", "port": 8005, "ui": null, "vramGB": 1,
          "healthy": false, "running": false, "pid": null, "installed": false, "configuredModels": [], "profiles": ["agent"] }
      ],
      "profiles": { "agent": ["llama-swap","tts","stt"], "coexist": ["llama-swap","fim"], "media": ["media","prompt-rewriter"] },
      "gpu": { "usedMB": 4749, "totalMB": 32607, "util": 4, "temp": 41, "watts": 74.85, "fan": 30, "perProcess": false, "activeGroup": "llm" }
    }
    """;

    [Fact]
    public void Parses_services_camelCase_and_nulls()
    {
        var doc = DokiService.ParseStatus(Sample);
        Assert.NotNull(doc);
        Assert.Equal(2, doc!.Services.Count);

        var ls = doc.Services[0];
        Assert.Equal("llama-swap", ls.Name);
        Assert.Equal(8080, ls.Port);
        Assert.Equal(26, ls.VramGb);
        Assert.True(ls.Healthy);
        Assert.Equal(12496, ls.Pid);
        Assert.Equal("coder-fast", ls.Model);
        Assert.Equal(3, ls.ConfiguredModels.Count);

        var stt = doc.Services[1];
        Assert.False(stt.Installed);
        Assert.Null(stt.Pid);
        Assert.Null(stt.Ui);
    }

    [Fact]
    public void Parses_gpu_and_profiles()
    {
        var doc = DokiService.ParseStatus(Sample)!;
        Assert.NotNull(doc.Gpu);
        Assert.Equal(4749, doc.Gpu!.UsedMB);
        Assert.Equal(32607, doc.Gpu.TotalMB);
        Assert.Equal("llm", doc.Gpu.ActiveGroup);
        Assert.Equal(30, doc.Gpu.Fan);
        Assert.False(doc.Gpu.PerProcess);
        Assert.Equal(3, doc.Profiles["agent"].Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    public void Returns_null_on_bad_input(string? json)
    {
        Assert.Null(DokiService.ParseStatus(json));
    }
}
