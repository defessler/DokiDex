using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// One pronunciation-dictionary rule: speak `From` as `To` (fix mispronounced names / invented fantasy words).
public sealed record LexRule(string? From, string? To);

// A browser request to synthesize speech. Engine is optional: null/blank/"chatterbox" => the coexisting-with-chat
// :8004 Chatterbox default (voice cloning + expressive knobs); "kokoro" => the GATED fast/light :8006 alternative
// (fixed preset voices, no cloning, knobs ignored). The chat /api/speak path passes no engine, so it stays
// Chatterbox byte-for-byte.
public sealed record SpeakRequest(string? Text, string? Voice, double Exaggeration = 0.5, double CfgWeight = 0.5,
    List<LexRule>? Lexicon = null, string? Engine = null);

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
    private const string Base = "http://127.0.0.1:8004";          // Chatterbox — the coexisting-with-chat DEFAULT
    private const string KokoroBase = "http://127.0.0.1:8006";    // GATED fast/light alternative (no cloning)
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly string[] AudioExt = { ".wav", ".mp3", ".flac", ".ogg" };
    // Chatterbox stores reference clips + predefined voices under these (config-configurable) folders; scan all.
    private static readonly string[] VoiceDirs = { "reference_audio", "voices", "predefined_voices", "reference" };

    private static string TtsRoot => Path.Combine(RepoPaths.Root, "tts", "Chatterbox-TTS-Server");

    public sealed record SpeakResult(bool Ok, string? ArtifactPath, string? Message);

    // Engine routing (pure). Only an explicit "kokoro" engine points the OpenAI body at the gated :8006 server;
    // everything else — null, blank, "chatterbox", or an unknown value — resolves to the :8004 Chatterbox DEFAULT
    // (the chat /api/speak path passes no engine, so it stays Chatterbox byte-for-byte).
    private static bool IsKokoro(string? engine) => string.Equals(engine?.Trim(), "kokoro", StringComparison.OrdinalIgnoreCase);
    public static string ResolveBase(string? engine) => IsKokoro(engine) ? KokoroBase : Base;
    public static string ModelFor(string? engine) => IsKokoro(engine) ? "kokoro" : "chatterbox";
    // The bind port, paired with ResolveBase so a future host/scheme change can't misattribute it (the old
    // LastIndexOf(':') slice off the base URL would break the moment a scheme/host gained another colon).
    public static string PortFor(string? engine) => IsKokoro(engine) ? "8006" : "8004";

    // Pronunciation dictionary: whole-word, case-insensitive alias substitution applied BEFORE synthesis — a
    // pure, model-agnostic, deterministic fix for the #1 TTS failure (mispronounced names/jargon/invented
    // anime-fantasy words). Pure + total -> unit-tested. (IPA/phoneme rules are gated on the tokenizer; this
    // alias slice ships now.)
    public static string ApplyLexicon(string text, IEnumerable<LexRule>? rules)
    {
        if (rules is null || string.IsNullOrEmpty(text)) return text;
        foreach (var r in rules)
        {
            var from = r.From?.Trim();
            if (string.IsNullOrEmpty(from)) continue;
            text = Regex.Replace(text, $@"\b{Regex.Escape(from)}\b", (r.To ?? "").Trim(), RegexOptions.IgnoreCase);
        }
        return text;
    }

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

    // Network-only synth: text -> wav bytes (no save, no lexicon — caller applies it). Shared by SpeakAsync and
    // multi-speaker dialogue. Degrades gracefully when :8004 is down.
    public static async Task<(bool Ok, byte[]? Audio, string? Error)> SynthBytesAsync(string text, string? voice, double exaggeration, double cfgWeight, CancellationToken ct, string? engine = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return (false, null, "empty text");
        var baseUrl = ResolveBase(engine);
        var port = PortFor(engine);   // known constant (:8004 / :8006) — not sliced from the URL
        // Chatterbox (:8004) takes the expressive knobs; Kokoro (:8006) has fixed preset voices and ignores them,
        // so the body drops them for Kokoro to keep its OpenAI shape clean.
        object body = IsKokoro(engine)
            ? new
            {
                model = ModelFor(engine),
                input = text,
                voice = string.IsNullOrWhiteSpace(voice) ? "default" : voice!.Trim(),
                response_format = "wav",
            }
            : new
            {
                model = ModelFor(engine),
                input = text,
                voice = string.IsNullOrWhiteSpace(voice) ? "default" : voice!.Trim(),
                response_format = "wav",
                exaggeration = exaggeration,
                cfg_weight = cfgWeight,
            };
        try
        {
            using var resp = await Http.PostAsJsonAsync($"{baseUrl}/v1/audio/speech", body, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"TTS returned {(int)resp.StatusCode} — check the voice name / model (start agent mode)");
            return (true, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) { return (false, null, "cancelled"); }
        catch (Exception ex) { return (false, null, $"TTS not reachable at :{port} — start agent mode first ({ex.Message})"); }
    }

    public static async Task<SpeakResult> SpeakAsync(SpeakRequest req, CancellationToken ct)
    {
        var text = ApplyLexicon((req.Text ?? "").Trim(), req.Lexicon);   // pronunciation dictionary (deterministic pre-synthesis fix)
        var (ok, audio, err) = await SynthBytesAsync(text, req.Voice, req.Exaggeration, req.CfgWeight, ct, req.Engine).ConfigureAwait(false);
        if (!ok || audio is null) return new SpeakResult(false, null, err);
        return await Save(audio, text, ct).ConfigureAwait(false);
    }

    // Multi-speaker dialogue: synth each parsed line with its speaker's voice + the tag's delivery, concatenate
    // (no ffmpeg — WavTools), save ONE clip to the Library. Lexicon applied per line. Fails fast if :8004 is down.
    public static async Task<SpeakResult> SpeakDialogueAsync(IReadOnlyList<DialogueLine> lines, IReadOnlyDictionary<string, string> cast, IEnumerable<LexRule>? lexicon, CancellationToken ct)
    {
        if (lines is null || lines.Count == 0) return new SpeakResult(false, null, "no dialogue lines");
        var clips = new List<byte[]>();
        foreach (var ln in lines)
        {
            var text = ApplyLexicon(ln.Text, lexicon);
            var voice = cast.TryGetValue(ln.Speaker, out var v) && !string.IsNullOrWhiteSpace(v) ? v : "default";
            var (ok, audio, err) = await SynthBytesAsync(text, voice, ln.Exaggeration, ln.CfgWeight, ct).ConfigureAwait(false);
            if (!ok) return new SpeakResult(false, null, err);
            if (audio is not null) clips.Add(audio);
        }
        var merged = WavTools.Concat(clips);
        if (merged is null) return new SpeakResult(false, null, "no audio produced");
        var preview = string.Join("  ", lines.Take(3).Select(l => $"{l.Speaker}: {l.Text}"));
        return await Save(merged, preview, ct, prefix: "dialogue").ConfigureAwait(false);
    }

    private static async Task<SpeakResult> Save(byte[] audio, string label, CancellationToken ct, string prefix = "speech")
    {
        try
        {
            var outPath = Path.Combine(DokiService.GenDir, $"{prefix}-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(DokiService.GenDir);
            await File.WriteAllBytesAsync(outPath, audio, ct).ConfigureAwait(false);
            GalleryService.WriteSidecar(outPath, "tts", "speech", label);   // lands in the Library as audio
            return new SpeakResult(true, outPath, "done");
        }
        catch (Exception ex) { return new SpeakResult(false, null, $"could not save the audio: {ex.Message}"); }
    }
}
