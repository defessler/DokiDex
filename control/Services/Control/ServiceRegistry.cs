using System.Collections.Generic;

namespace DokiDex.Control.Services;

// One service in the native control plane. Mirrors a row of doki.ps1's $Services.
public sealed record ServiceDef(
    string Name, string Group, string Desc, int Port, string? Ui,
    int VramGb, string Health, string LaunchScript, string? RequiresRel);

// C# mirror of doki.ps1's $Services / $Profiles — the source of truth for the NATIVE control plane (status
// probing + lifecycle) so the everyday poll never shells pwsh. ControlPlaneTests asserts this stays in sync
// with doki.ps1 (same service names + profiles), so adding a service there fails the test until mirrored here.
public static class ServiceRegistry
{
    public static readonly IReadOnlyList<ServiceDef> Services = new[]
    {
        new ServiceDef("llama-swap", "llm", "agent inference :8080", 8080, "http://127.0.0.1:8080/ui", 26, "http://127.0.0.1:8080/v1/models", "start-serving.ps1", null),
        new ServiceDef("fim", "llm", "autocomplete  :8012", 8012, null, 4, "http://127.0.0.1:8012/health", "start-fim.ps1", null),
        new ServiceDef("embed", "llm", "code embeddings :8090", 8090, null, 0, "http://127.0.0.1:8090/health", "start-embed.ps1", @"models\nomic-embed-text-v1.5.f16.gguf"),
        new ServiceDef("tts", "llm", "speech/TTS    :8004", 8004, "http://127.0.0.1:8004/", 4, "http://127.0.0.1:8004/", "start-tts.ps1", @"tts\Chatterbox-TTS-Server\.venv\Scripts\python.exe"),
        new ServiceDef("stt", "llm", "speech-to-text :8005", 8005, null, 1, "http://127.0.0.1:8005/health", "start-stt.ps1", @"stt\.venv\Scripts\python.exe"),
        new ServiceDef("media", "media", "image+video   :7801", 7801, "http://127.0.0.1:7801/", 18, "http://127.0.0.1:7801/", "start-media.ps1", null),
        new ServiceDef("prompt-rewriter", "media", "prompt rewriter :8013", 8013, null, 3, "http://127.0.0.1:8013/health", "start-prompt-rewriter.ps1", @"models\Qwen2.5-3B-Instruct-Q5_K_M.gguf"),
    };

    public static readonly IReadOnlyDictionary<string, List<string>> Profiles = new Dictionary<string, List<string>>
    {
        ["agent"]   = new() { "llama-swap", "tts", "stt", "embed" },
        ["coexist"] = new() { "llama-swap", "fim", "embed" },
        ["media"]   = new() { "media", "prompt-rewriter" },
    };

    public static ServiceDef? Find(string name)
    {
        foreach (var s in Services) if (s.Name == name) return s;
        return null;
    }

    // GPU is one group at a time: the media profile is the media group, everything else is the llm group.
    public static string GroupForProfile(string profile) => profile == "media" ? "media" : "llm";
}
