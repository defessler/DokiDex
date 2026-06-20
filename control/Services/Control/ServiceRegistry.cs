using System.Collections.Generic;

namespace DokiDex.Control.Services;

// One service in the native control plane. Mirrors a row of doki.ps1's $Services.
// SetupHint = the exact installer command the WPF card shows when the service isn't installed (registry-driven,
// so a new gated service can never fall back to a wrong hint). It is a C#-side UI concern, not mirrored in
// doki.ps1 (the CLI shows the desc, not a hint) — ControlPlaneTests checks names+profiles, not fields.
public sealed record ServiceDef(
    string Name, string Group, string Desc, int Port, string? Ui,
    int VramGb, string Health, string LaunchScript, string? RequiresRel, string SetupHint = "setup.ps1");

// C# mirror of doki.ps1's $Services / $Profiles — the source of truth for the NATIVE control plane (status
// probing + lifecycle) so the everyday poll never shells pwsh. ControlPlaneTests asserts this stays in sync
// with doki.ps1 (same service names + profiles), so adding a service there fails the test until mirrored here.
public static class ServiceRegistry
{
    public static readonly IReadOnlyList<ServiceDef> Services = new[]
    {
        // llama-swap + fim are core (no RequiresRel) so they never hit the not-installed hint; the base
        // installer fetches the embed model (no flag), so its hint stays the default "setup.ps1".
        new ServiceDef("llama-swap", "llm", "agent inference :8080", 8080, "http://127.0.0.1:8080/ui", 26, "http://127.0.0.1:8080/v1/models", "start-serving.ps1", null),
        new ServiceDef("fim", "llm", "autocomplete  :8012", 8012, null, 4, "http://127.0.0.1:8012/health", "start-fim.ps1", null),
        // embed (:8090) now powers BOTH the codebase RAG (code_search) AND the KB "chat with your documents" RAG.
        new ServiceDef("embed", "llm", "embeddings code+KB :8090", 8090, null, 0, "http://127.0.0.1:8090/health", "start-embed.ps1", @"models\nomic-embed-text-v1.5.f16.gguf"),
        new ServiceDef("tts", "llm", "speech/TTS    :8004", 8004, "http://127.0.0.1:8004/", 4, "http://127.0.0.1:8004/", "start-tts.ps1", @"tts\Chatterbox-TTS-Server\.venv\Scripts\python.exe", "setup.ps1 -Tts"),
        // GATED fast/light TTS alternative (Kokoro-82M via remsky/Kokoro-FastAPI) — Apache-2.0, <2GB VRAM, NO
        // cloning. Additive on :8006, group=llm so it coexists. NOT in any default profile; RequiresRel skips it
        // cleanly until -Kokoro installs it. The :8004 Chatterbox server stays the coexisting-with-chat DEFAULT.
        new ServiceDef("kokoro", "llm", "Kokoro TTS   :8006", 8006, "http://127.0.0.1:8006/web", 2, "http://127.0.0.1:8006/health", "start-kokoro.ps1", @"kokoro\Kokoro-FastAPI\.venv\Scripts\python.exe", "setup.ps1 -Kokoro"),
        new ServiceDef("stt", "llm", "speech-to-text :8005", 8005, null, 1, "http://127.0.0.1:8005/health", "start-stt.ps1", @"stt\.venv\Scripts\python.exe", "setup.ps1 -Stt"),
        new ServiceDef("media", "media", "image+video   :7801", 7801, "http://127.0.0.1:7801/", 18, "http://127.0.0.1:7801/", "start-media.ps1", null, "setup.ps1 -Media -Models full"),
        new ServiceDef("prompt-rewriter", "media", "prompt rewriter :8013", 8013, null, 3, "http://127.0.0.1:8013/health", "start-prompt-rewriter.ps1", @"models\Qwen2.5-3B-Instruct-Q5_K_M.gguf", "setup.ps1 -Media -Models full"),
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
