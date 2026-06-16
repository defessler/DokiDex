namespace DokiDex.Control.Services;

// A text->media request for `doki gen`, and the pure translation of it into doki.ps1's argv.
//
// This is the GPU-free heart of the DokiGen Studio: GenCli.BuildArgs is total + side-effect-free, so the
// exact command the panel will shell is locked by unit tests (GenCliTests) with no card. Only the live run
// (DokiService.RunGenAsync) needs media mode. The contract mirrors serving/doki-gen.ps1 1:1.
public sealed record GenRequest(
    string Prompt,
    string Kind,                       // image | video | music | edit | i2v | foley
    bool Fast = false,
    bool Upscale = false,
    bool Raw = false,
    string? InitImage = null,
    string OutPath = "",
    bool Refine = false,
    bool Face = false,
    bool Realism = false,
    int Seed = -1,            // >=0 = reproducible; -1 = random (SwarmUI picks)
    int Count = 1,            // batch size (images)
    double Strength = -1,     // img2img/i2v creativity (the "vary" dial); -1 = recipe default
    string? MaskImage = null, // inpaint mask (edit canvas); white = the region to change
    string? Aspect = null)    // aspect-ratio preset (16:9 / 9:16 / 4:3 / 3:4 / 1:1) for image/edit
{
    // the picker's kinds, in order, 1:1 with doki-gen.ps1 Resolve-GenKind.
    public static readonly string[] Kinds = { "image", "video", "music", "edit", "i2v", "foley" };

    // -Upscale (4x-UltraSharp) is a still-image post pass only; doki-gen.ps1 throws for other kinds.
    public static bool UpscaleApplies(string kind) => kind is "image" or "edit";
    // -Edit is an instruction over an existing image — doki-gen.ps1 requires -InitImage for it.
    public static bool RequiresInitImage(string kind) => kind is "edit";
    // the panel can show an image inline today; video/music open externally (inline MediaElement is phase 4).
    public static bool IsInlineImageKind(string kind) => kind is "image" or "edit";

    // the extension doki writes to -Out for this kind's PRIMARY artifact (Invoke-Gen prefers the real
    // media file over the preview still). Used to name the temp file and pick the preview path.
    public static string OutExtensionFor(string kind) => kind switch
    {
        "video" or "i2v" or "foley" => ".mp4",
        "music"                     => ".mp3",
        _                           => ".png",   // image, edit
    };

    public bool IsInline => IsInlineImageKind(Kind);
}

// The outcome of a live gen run: the artifact path doki saved (if Ok) + a short human message.
public sealed record GenResult(bool Ok, string OutPath, string Message);

public static class GenCli
{
    // kind -> the doki.ps1 switch; "image" is the default (no switch).
    private static readonly Dictionary<string, string> KindSwitch = new()
    {
        ["video"] = "-Video", ["music"] = "-Music", ["edit"] = "-Edit", ["i2v"] = "-I2v", ["foley"] = "-Foley",
    };

    // Build the exact `doki.ps1 gen …` argv for a request. Pure + total -> unit-tested with no GPU.
    // Modifiers that the CLI would reject for a kind are dropped here (never send a doomed command);
    // -NoOpen is always added (the panel previews inline / opens on demand, no browser pop).
    public static List<string> BuildArgs(GenRequest r)
    {
        var a = new List<string> { "gen", r.Prompt };
        if (KindSwitch.TryGetValue(r.Kind, out var sw)) a.Add(sw);
        if (r.Fast) a.Add("-Fast");
        if (r.Upscale && GenRequest.UpscaleApplies(r.Kind)) a.Add("-Upscale");
        if (r.Refine && GenRequest.UpscaleApplies(r.Kind)) a.Add("-Refine");
        if (r.Face) a.Add("-Face");
        if (r.Realism) a.Add("-Realism");
        if (r.Raw) a.Add("-Raw");
        if (!string.IsNullOrWhiteSpace(r.InitImage)) { a.Add("-InitImage"); a.Add(r.InitImage!); }
        if (!string.IsNullOrWhiteSpace(r.MaskImage)) { a.Add("-MaskImage"); a.Add(r.MaskImage!); }
        if (!string.IsNullOrWhiteSpace(r.Aspect)) { a.Add("-Aspect"); a.Add(r.Aspect!); }
        if (!string.IsNullOrWhiteSpace(r.OutPath)) { a.Add("-Out"); a.Add(r.OutPath); }
        if (r.Seed >= 0) { a.Add("-Seed"); a.Add(r.Seed.ToString()); }
        if (r.Count > 1) { a.Add("-Count"); a.Add(r.Count.ToString()); }
        if (r.Strength >= 0) { a.Add("-Strength"); a.Add(r.Strength.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        a.Add("-NoOpen");
        return a;
    }
}
