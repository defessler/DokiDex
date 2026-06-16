namespace DokiDex.Web;

// A camera move for a video shot: an optional named preset + per-axis signed sliders (-10..10).
public sealed record CameraSpec(string? Preset, int Pan = 0, int Tilt = 0, int Zoom = 0, int Roll = 0);

// Deterministic cinematography → prompt tokens. Local video models have no motion-conditioning node here, so
// the backlog's call is "compile to prompt tokens — zero new model": this turns structured camera controls
// (a preset + signed pan/tilt/zoom/roll) into a natural-language camera phrase appended to a video prompt.
// Pure + total → unit-tested with no GPU; the SPA shows the controls for video/i2v and appends the phrase.
public static class Camera
{
    // named macro moves -> a phrase (the dependable, opinionated presets)
    private static readonly Dictionary<string, string> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orbit"]      = "orbiting camera",
        ["crane-up"]   = "crane shot rising up",
        ["crane-down"] = "crane shot descending",
        ["dolly-in"]   = "dolly in",
        ["dolly-out"]  = "dolly out",
        ["bullet-time"]= "bullet-time rotation",
        ["handheld"]   = "handheld shaky cam",
        ["static"]     = "static locked-off shot",
    };

    public static string Phrase(CameraSpec s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Preset) && Presets.TryGetValue(s.Preset.Trim(), out var pre)) parts.Add(pre);

        Axis(parts, s.Pan, "pan left", "pan right");
        Axis(parts, s.Tilt, "tilt down", "tilt up");
        Axis(parts, s.Zoom, "zoom out", "zoom in");
        Axis(parts, s.Roll, "roll counter-clockwise", "roll clockwise");

        return string.Join(", ", parts);
    }

    // signed slider -> "<intensity> <direction>" (negative => negText, positive => posText); 0 contributes nothing.
    private static void Axis(List<string> into, int v, string negText, string posText)
    {
        if (v == 0) return;
        var mag = Math.Abs(v);
        var intensity = mag <= 3 ? "slow " : mag <= 7 ? "" : "fast ";   // mid = the plain verb
        into.Add(intensity + (v < 0 ? negText : posText));
    }
}
