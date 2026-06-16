using System.IO;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure, file-based part of the TTS voice registry: scanning Chatterbox's reference/voice folders for
// clips. The synth call needs the :8004 server, but the scan is GPU-free and locked here.
public class TtsTests : IDisposable
{
    private readonly string _root;
    public TtsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dokidex-tts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "reference_audio"));
        Directory.CreateDirectory(Path.Combine(_root, "voices"));
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string sub, string name) => File.WriteAllBytes(Path.Combine(_root, sub, name), new byte[] { 0 });

    [Fact]
    public void Scans_audio_clips_across_candidate_folders_as_voice_names()
    {
        Write("reference_audio", "hero.wav");
        Write("voices", "narrator.mp3");
        var voices = Tts.Voices(_root);
        Assert.Contains("hero", voices);
        Assert.Contains("narrator", voices);   // names = filename without extension, across folders
    }

    [Fact]
    public void Non_audio_files_are_ignored()
    {
        Write("voices", "readme.txt");
        Write("voices", "good.wav");
        var voices = Tts.Voices(_root);
        Assert.DoesNotContain("readme", voices);
        Assert.Contains("good", voices);
    }

    [Fact]
    public void Duplicate_names_across_folders_collapse()
    {
        Write("reference_audio", "twin.wav");
        Write("voices", "twin.wav");
        Assert.Equal(1, Tts.Voices(_root).Count(v => v == "twin"));
    }

    [Fact]
    public void Missing_tts_install_yields_no_voices()
        => Assert.Empty(Tts.Voices(Path.Combine(_root, "does-not-exist")));

    // --- pronunciation dictionary (ApplyLexicon) ---
    [Fact]
    public void Lexicon_replaces_whole_words_case_insensitively()
    {
        var rules = new[] { new LexRule("Caelum", "KYE-lum") };
        Assert.Equal("hail KYE-lum and KYE-lum", Tts.ApplyLexicon("hail Caelum and caelum", rules));
    }

    [Fact]
    public void Lexicon_does_not_replace_inside_a_larger_word()
        => Assert.Equal("decode", Tts.ApplyLexicon("decode", new[] { new LexRule("code", "kohd") }));   // \b guards

    [Fact]
    public void Lexicon_handles_multi_word_aliases_and_multiple_rules()
    {
        var rules = new[] { new LexRule("Ymir", "EE-meer"), new LexRule("dark lord", "overlord") };
        Assert.Equal("EE-meer the overlord", Tts.ApplyLexicon("Ymir the dark lord", rules));
    }

    [Fact]
    public void Lexicon_null_or_blank_rules_are_a_no_op()
    {
        Assert.Equal("unchanged", Tts.ApplyLexicon("unchanged", null));
        Assert.Equal("unchanged", Tts.ApplyLexicon("unchanged", new[] { new LexRule("  ", "x") }));
    }
}
