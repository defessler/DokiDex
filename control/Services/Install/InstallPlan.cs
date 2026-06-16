using System.Collections.Generic;

namespace DokiDex.Control.Services;

// The components a fresh install can pull in, mapped to setup.ps1 args + the separate coder-model download.
// Core (config deploy + the small embed model) always runs, so it has no flag.
public sealed record InstallChoice(
    bool CoderModels = true,   // download_models.py — the ~30B + 120B coder GGUFs (~80 GB)
    bool Media = true,         // -Media — SwarmUI + the image/video/audio stack
    bool ModelsFull = true,    // -Models full — the ~90–100 GB quality kit (else the lean set)
    bool Tts = false,          // -Tts
    bool Stt = false);         // -Stt

public static class InstallPlan
{
    // setup.ps1 args for a choice (pure; unit-tested). -Models full only applies alongside -Media.
    public static List<string> SetupArgs(InstallChoice c)
    {
        var a = new List<string>();
        if (c.Media) a.Add("-Media");
        if (c.Media && c.ModelsFull) { a.Add("-Models"); a.Add("full"); }
        if (c.Tts) a.Add("-Tts");
        if (c.Stt) a.Add("-Stt");
        return a;
    }

    // Rough disk estimate (GB) for the free-space check + the wizard summary.
    public static int EstimateGb(InstallChoice c)
    {
        var gb = 1;                                  // core configs + the embed model
        if (c.CoderModels) gb += 80;                 // 30B (~18) + 120B (~60)
        if (c.Media) gb += c.ModelsFull ? 100 : 15;  // full quality kit vs the lean set
        if (c.Tts) gb += 3;
        if (c.Stt) gb += 1;
        return gb;
    }
}
