using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A browser request to synthesize speech.
public sealed record SpeakRequest(string? Text, string? Voice, double Exaggeration = 0.5, double CfgWeight = 0.5);

// Local text-to-speech over the shipped Chatterbox server (OpenAI-compatible /v1/audio/speech on :8004 — llm
// group, coexists with the coder LLM). Two pieces:
//   • a FILE-BASED voice registry (scan Chatterbox's reference/voice folders for clips, like the gallery /
//     model manager) so cloned + predefined voices show up with no server call; and
//   • SpeakAsync: POST text -> wav, saved into the app's gen folder (+ sidecar) so it lands in the Library.
// The synth call degrades gracefully when :8004 is down (mirrors SwarmGen / the Director). The voice-folder
// scan is robust to the exact layout (candidates) since it can't be verified headless; the OpenAI speech body
// is standard.
public static class Tts
{
    private const string Base = "http://127.0.0.1:8004";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly string[] AudioExt = { ".wav", ".mp3", ".flac", ".ogg" };
    // Chatterbox stores reference clips + predefined voices under these (config-configurable) folders; scan all.
    private static readonly string[] VoiceDirs = { "reference_audio", "voices", "predefined_voices", "reference" };

    private static string TtsRoot => Path.Combine(RepoPaths.Root, "tts", "Chatterbox-TTS-Server");

    public sealed record SpeakResult(bool Ok, string? ArtifactPath, string? Message);

    // The voice registry: distinct voice NAMES (filename without extension) found across the candidate folders.
    public static IEnumerable<string> Voices() => Voices(TtsRoot);

    public static IReadOnlyList<string> Voices(string ttsRoot)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in VoiceDirs)
        {
            var dir = Path.Combine(ttsRoot, d);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                if (AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    names.Add(Path.GetFileNameWithoutExtension(f));
        }
        return names.ToList();
    }

    public static async Task<SpeakResult> SpeakAsync(SpeakRequest req, CancellationToken ct)
    {
        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) return new SpeakResult(false, null, "empty text");

        // OpenAI-compatible speech body (+ Chatterbox's exaggeration / cfg_weight expressivity dials).
        var body = new
        {
            model = "chatterbox",
            input = text,
            voice = string.IsNullOrWhiteSpace(req.Voice) ? "default" : req.Voice!.Trim(),
            response_format = "wav",
            exaggeration = req.Exaggeration,
            cfg_weight = req.CfgWeight,
        };

        byte[] audio;
        try
        {
            using var resp = await Http.PostAsJsonAsync($"{Base}/v1/audio/speech", body, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new SpeakResult(false, null, $"TTS returned {(int)resp.StatusCode} — check the voice name / model (start agent mode)");
            audio = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new SpeakResult(false, null, "cancelled"); }
        catch (Exception ex) { return new SpeakResult(false, null, $"TTS not reachable at :8004 — start agent mode first ({ex.Message})"); }

        try
        {
            var outPath = Path.Combine(DokiService.GenDir, $"speech-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(DokiService.GenDir);
            await File.WriteAllBytesAsync(outPath, audio, ct).ConfigureAwait(false);
            GalleryService.WriteSidecar(outPath, "tts", "speech", text);   // lands in the Library as audio
            return new SpeakResult(true, outPath, "done");
        }
        catch (Exception ex) { return new SpeakResult(false, null, $"could not save the audio: {ex.Message}"); }
    }
}
