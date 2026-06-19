using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Control.Services;

// One ControlNet unit: a structure model + control image (path/data) + strength + preprocessor. Up to 3
// stack (SwarmUI ControlNet / Two / Three); each is independent so no parallel-array alignment.
public sealed record ControlUnit(string? Model, string? Image = null, double Strength = 1, string? Preprocessor = null);

// A text->media request for `doki gen`, and the pure translation of it into doki.ps1's argv.
//
// This is the GPU-free heart of the DokiGen Studio: GenCli.BuildArgs is total + side-effect-free, so the
// exact command the panel will shell is locked by unit tests (GenCliTests) with no card. Only the live run
// (DokiService.RunGenAsync) needs media mode. The contract mirrors serving/doki-gen.ps1 1:1.
public sealed record GenRequest(
    string Prompt,
    string Kind,                       // image | video | music | edit | i2v | foley
    bool Fast = false,
    bool Quality = false,      // music: opt-in hi-fi swap (turbo default -> ACE-Step 1.5 XL base); music kind only
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
    string? Aspect = null,    // aspect-ratio preset (16:9 / 9:16 / 4:3 / 3:4 / 1:1) for image/edit
    string? Lyrics = null,    // music: lyrics ACE-Step sings (else [instrumental]); music kind only
    int Duration = 0,         // music: track length in seconds (0 = recipe default 10)
    int Bpm = 0,              // music: tempo override (0 = recipe default 128)
    string? Lora = null,      // LoRA mixer: "name:0.8,other" -> <lora:..> tags; image-family only
    string? Negative = null,  // user negative prompt: appended to (image) or set as the recipe negativeprompt
    string? Upscaler = null,  // upscale engine (balanced/photo/anime) or a raw model file; needs -Upscale/-Refine
    string? Segment = null,   // promptable region refine: "hair,hands:0.6" -> <segment:..> tags; image-family
    System.Collections.Generic.IReadOnlyList<ControlUnit>? ControlNets = null,  // 1-3 stacked ControlNet units; image/edit only
    string? EndImage = null,           // FLF2V end keyframe (video/i2v); needs an end-frame-capable model
    bool Reference = false,            // use the init image as an IP-Adapter style/subject reference (image/edit)
    double RefWeight = 0.6,            // IP-Adapter reference weight
    string? Interpolate = null,        // video frame interpolation method (RIFE/FILM/GIMM); video/i2v only
    int InterpolateMult = 2,           // interpolation multiplier
    string? Workflow = null,           // run an installed SwarmUI custom ComfyUI workflow by name (SUPIR/InstantID/own)
    string? Tile = null,               // seamless-tileable output (true/x/y); image/edit only
    string? Model = null)              // checkpoint override (manual picker / Auto router); null = recipe default
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
        // -Quality is the music-only hi-fi opt-in (doki-gen.ps1 swaps turbo -> ACE-Step 1.5 XL base); the
        // recipe ignores it for other kinds, so don't emit a doomed switch there.
        if (r.Quality && r.Kind is "music") a.Add("-Quality");
        if (r.Upscale && GenRequest.UpscaleApplies(r.Kind)) a.Add("-Upscale");
        if (r.Refine && GenRequest.UpscaleApplies(r.Kind)) a.Add("-Refine");
        // engine selector only matters when a post-pass runs (image/edit + -Upscale/-Refine); else it's dropped
        if (!string.IsNullOrWhiteSpace(r.Upscaler) && (r.Upscale || r.Refine) && GenRequest.UpscaleApplies(r.Kind)) { a.Add("-Upscaler"); a.Add(r.Upscaler!); }
        if (r.Face) a.Add("-Face");
        if (r.Realism) a.Add("-Realism");
        if (r.Raw) a.Add("-Raw");
        if (!string.IsNullOrWhiteSpace(r.InitImage)) { a.Add("-InitImage"); a.Add(r.InitImage!); }
        if (!string.IsNullOrWhiteSpace(r.MaskImage)) { a.Add("-MaskImage"); a.Add(r.MaskImage!); }
        if (!string.IsNullOrWhiteSpace(r.Aspect)) { a.Add("-Aspect"); a.Add(r.Aspect!); }
        if (!string.IsNullOrWhiteSpace(r.Lyrics)) { a.Add("-Lyrics"); a.Add(r.Lyrics!); }
        if (r.Duration > 0) { a.Add("-Duration"); a.Add(r.Duration.ToString()); }
        if (r.Bpm > 0) { a.Add("-Bpm"); a.Add(r.Bpm.ToString()); }
        if (!string.IsNullOrWhiteSpace(r.Lora)) { a.Add("-Lora"); a.Add(r.Lora!); }
        if (!string.IsNullOrWhiteSpace(r.Negative)) { a.Add("-Negative"); a.Add(r.Negative!); }
        if (!string.IsNullOrWhiteSpace(r.Segment)) { a.Add("-Segment"); a.Add(r.Segment!); }
        // ControlNet stacking (up to 3): a unit with a model activates it. Serialized as one -ControlNets JSON
        // arg (image = path); image/edit only. doki-gen maps unit 0/1/2 -> controlnet / controlnettwo / controlnetthree.
        if (r.ControlNets is { Count: > 0 } && GenRequest.UpscaleApplies(r.Kind))
        {
            var units = r.ControlNets.Where(u => !string.IsNullOrWhiteSpace(u.Model)).Take(3).ToList();
            if (units.Count > 0) { a.Add("-ControlNets"); a.Add(System.Text.Json.JsonSerializer.Serialize(units)); }
        }
        if (!string.IsNullOrWhiteSpace(r.EndImage) && r.Kind is "video" or "i2v") { a.Add("-EndImage"); a.Add(r.EndImage!); }
        // IP-Adapter image reference: only with an init image on image/edit (the init image is the reference)
        if (r.Reference && !string.IsNullOrWhiteSpace(r.InitImage) && GenRequest.UpscaleApplies(r.Kind))
        {
            a.Add("-Reference");
            a.Add("-RefWeight"); a.Add(r.RefWeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(r.Interpolate) && r.Kind is "video" or "i2v")
        {
            a.Add("-Interpolate"); a.Add(r.Interpolate!);
            a.Add("-InterpolateMult"); a.Add(r.InterpolateMult.ToString());
        }
        if (!string.IsNullOrWhiteSpace(r.Workflow)) { a.Add("-Workflow"); a.Add(r.Workflow!); }
        if (!string.IsNullOrWhiteSpace(r.Tile) && GenRequest.UpscaleApplies(r.Kind)) { a.Add("-Tile"); a.Add(r.Tile!); }
        if (!string.IsNullOrWhiteSpace(r.Model)) { a.Add("-Model"); a.Add(r.Model!); }
        if (!string.IsNullOrWhiteSpace(r.OutPath)) { a.Add("-Out"); a.Add(r.OutPath); }
        if (r.Seed >= 0) { a.Add("-Seed"); a.Add(r.Seed.ToString()); }
        if (r.Count > 1) { a.Add("-Count"); a.Add(r.Count.ToString()); }
        if (r.Strength >= 0) { a.Add("-Strength"); a.Add(r.Strength.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        a.Add("-NoOpen");
        return a;
    }
}
