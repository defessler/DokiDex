using DokiDex.Control.Models;

namespace DokiDex.Control.Services;

// Canned `doki status json` for the panel's --design / DOKI_SAMPLE mode: exercises the full state
// vocabulary with NO backend — healthy + degraded(pulse) + crashed* + down(stopped) + not-installed(ghost),
// model-swap chips, both DokiCode/DokiGen bands, a live-looking GPU meter. (*crashed is forced in
// MainViewModel.LoadDesignSample, since it is time-derived and unreachable from a single Apply.)
internal static class SampleData
{
    public static StatusDoc Status() => new()
    {
        Gpu = new GpuStatus { UsedMB = 25800, TotalMB = 32607, Util = 78, Temp = 64, Watts = 318, Fan = 62, ActiveGroup = "llm" },
        Profiles = new()
        {
            ["agent"]   = new() { "llama-swap", "tts", "stt" },
            ["coexist"] = new() { "llama-swap", "fim" },
            ["media"]   = new() { "media", "prompt-rewriter" },
        },
        Services = new()
        {
            // DokiCode (llm) band — active/bright
            new() { Name = "llama-swap", Group = "llm", Installed = true, Running = true, Healthy = true,  Port = 8080, Ui = "http://127.0.0.1:8080/ui", VramGb = 26, Pid = 12344, Model = "coder-fast", ConfiguredModels = new() { "coder-big", "coder-fast", "coder-fast-lite" }, Version = "llama-swap v224" },
            new() { Name = "fim",        Group = "llm", Installed = true, Running = true, Healthy = false, Port = 8012, VramGb = 5, Pid = 12880, Model = "qwen2.5-coder-3b" },               // degraded -> calm pulse
            new() { Name = "embed",      Group = "llm", Installed = true, Running = true, Healthy = true,  Port = 8090, VramGb = 0, Pid = 12990, Model = "nomic-embed-text-v1.5" },          // RAG code_search (CPU, 0 VRAM)
            new() { Name = "tts",        Group = "llm", Installed = true, Running = false, Healthy = false, Port = 8004, Ui = "http://127.0.0.1:8004/", VramGb = 4 },                            // installed but stopped -> down (▶ start)
            new() { Name = "stt",        Group = "llm", Installed = true, Running = true, Healthy = false, Port = 8005, VramGb = 1, Pid = 13422, Model = "parakeet" },                         // forced -> crashed (red alarm)
            // DokiGen (media) band — recessed
            new() { Name = "media",           Group = "media", Installed = true,  Running = true, Healthy = true, Port = 7801, Ui = "http://127.0.0.1:7801/", VramGb = 18, Pid = 9001, Model = "Z-Image-Turbo", Update = "3 behind" },
            new() { Name = "prompt-rewriter", Group = "media", Installed = false },                                                                                                          // not installed -> ghost
        },
    };
}
