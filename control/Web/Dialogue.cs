using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DokiDex.Web;

// One parsed dialogue line: who speaks, what they say (tags stripped), and the delivery the inline performance
// tag selected (Chatterbox exaggeration / cfg_weight).
public sealed record DialogueLine(string Speaker, string Text, double Exaggeration, double CfgWeight);

public sealed record DialogueCast(string? Speaker, string? Voice);
public sealed record DialogueRequest(string? Script, List<DialogueCast>? Cast, List<LexRule>? Lexicon);

// Multi-speaker dialogue scripting over Chatterbox: a "HERO: hello" script becomes per-line synth calls routed
// to each speaker's voice and concatenated. The pure, unit-tested half is Parse — it splits named-speaker lines,
// carries an unlabeled line forward as the same speaker, and compiles inline performance tags ([excited],
// [whispers], …) to a delivery. (Per-speaker prosody/overlap isn't possible — Chatterbox renders lines
// independently — so `[interrupting]`-style tags degrade to sequential turns; documented, not promised.)
public static class Dialogue
{
    private const double DefEx = 0.5, DefCfg = 0.5;

    // first recognized tag on a line sets its delivery; all tags are stripped from the spoken text.
    private static readonly Dictionary<string, (double Ex, double Cfg)> Tags = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["excited"] = (0.85, 0.5), ["happy"] = (0.8, 0.5), ["shout"] = (0.95, 0.6), ["yell"] = (0.95, 0.6),
        ["angry"] = (0.9, 0.6), ["whisper"] = (0.3, 0.3), ["whispers"] = (0.3, 0.3), ["soft"] = (0.35, 0.4),
        ["sad"] = (0.4, 0.45), ["calm"] = (0.4, 0.5), ["neutral"] = (0.45, 0.5), ["laughs"] = (0.7, 0.5), ["laugh"] = (0.7, 0.5),
    };

    private static readonly Regex SpeakerLine = new(@"^\s*([A-Za-z][A-Za-z0-9 _'\-]{0,29}):\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"\[([A-Za-z]+)\]", RegexOptions.Compiled);

    public static List<DialogueLine> Parse(string? script)
    {
        var outp = new List<DialogueLine>();
        if (string.IsNullOrWhiteSpace(script)) return outp;
        var current = "Narrator";
        foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            string text;
            var m = SpeakerLine.Match(line);
            if (m.Success) { current = m.Groups[1].Value.Trim(); text = m.Groups[2].Value; }
            else text = line;   // unlabeled => continues the current speaker

            // delivery from the first recognized tag; strip ALL tags from what's spoken
            double ex = DefEx, cfg = DefCfg; bool set = false;
            foreach (Match t in Tag.Matches(text))
                if (!set && Tags.TryGetValue(t.Groups[1].Value, out var d)) { ex = d.Ex; cfg = d.Cfg; set = true; }
            text = Tag.Replace(text, "").Trim();
            text = Regex.Replace(text, @"\s{2,}", " ");
            if (text.Length == 0) continue;
            outp.Add(new DialogueLine(current, text, ex, cfg));
        }
        return outp;
    }
}
