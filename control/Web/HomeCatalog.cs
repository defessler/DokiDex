using System;
using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// What a capability needs to be usable RIGHT NOW. Any field null = "no requirement of that kind".
//   Mode    — the GPU group it needs resident ('agent' or 'media'); the single 32GB GPU is one-group-at-a-time.
//   Service — a service that must be up (e.g. 'tts').   Model — a model that must be installed.
public sealed record CapabilityRequires(string? Mode, string? Service, string? Model);

// A clickable example that launches an area pre-filled (teaches by doing). View = a Studio view id; Prompt = optional
// text to drop into that view's prompt box; Kind = optional gen kind for the Create view (image/video/music/edit/i2v).
public sealed record CapabilityStarter(string Label, string View, string? Prompt = null, string? Kind = null);

// One capability area on the guided Home hub.
public sealed record HomeCapability(
    string Id, string Group, string Name, string Icon, string Blurb,
    CapabilityRequires Requires, IReadOnlyList<CapabilityStarter> Starters);

// A snapshot of live state the readiness resolver joins against (built by GET /api/home from the status probe).
public sealed record HomeStatusSnapshot(string? Mode, ISet<string> ServicesUp, ISet<string> ModelsPresent);

// The computed "can I use this now" verdict for one card. Status: ready | needs-mode | needs-setup. NextStep is the
// human one-liner; Action is a machine hint the SPA turns into a button ("mode:media" / "service:tts" / "model:vision").
public sealed record HomeReadiness(string Status, string? NextStep, string? Action);

// One capability + its computed readiness — the GET /api/home item the SPA renders.
public sealed record HomeCard(HomeCapability Capability, HomeReadiness Readiness);

// The guided Home hub's content catalog + the PURE readiness resolver. The catalog is the single source of truth for
// what the hub shows (data, not logic); the resolver (requires + status snapshot -> readiness) is a pure, unit-tested
// function so the "ready / needs-X" logic is covered like the rest of the stack. The SPA Home view renders /api/home
// with no logic of its own. Add or refine an area by editing the catalog below.
public static class HomeCatalog
{
    public static readonly HomeReadiness Ready = new("ready", null, null);

    // PURE: requires + a live snapshot -> readiness. Precedence: a mode mismatch dominates (a service/model can't be
    // used in the wrong GPU group anyway), then a missing service, then a missing model; otherwise ready. Never throws.
    public static HomeReadiness Resolve(CapabilityRequires requires, HomeStatusSnapshot snap)
    {
        if (requires is null) return Ready;
        if (!string.IsNullOrWhiteSpace(requires.Mode) && !string.Equals(snap?.Mode, requires.Mode, StringComparison.OrdinalIgnoreCase))
            return new HomeReadiness("needs-mode", $"Switch to {requires.Mode} mode", $"mode:{requires.Mode}");
        if (!string.IsNullOrWhiteSpace(requires.Service) && snap?.ServicesUp?.Contains(requires.Service!) != true)
            return new HomeReadiness("needs-setup", $"Start the {requires.Service} service", $"service:{requires.Service}");
        if (!string.IsNullOrWhiteSpace(requires.Model) && snap?.ModelsPresent?.Contains(requires.Model!) != true)
            return new HomeReadiness("needs-setup", $"Install the {requires.Model} model", $"model:{requires.Model}");
        return Ready;
    }

    // Annotate every catalog capability with its readiness against a snapshot (the GET /api/home payload).
    public static IReadOnlyList<HomeCard> Annotate(HomeStatusSnapshot snap)
        => Capabilities.Select(c => new HomeCard(c, Resolve(c.Requires, snap))).ToList();

    // terse catalog helpers
    private static CapabilityRequires Need(string? mode = null, string? service = null, string? model = null) => new(mode, service, model);
    private static CapabilityStarter S(string label, string view, string? prompt = null, string? kind = null) => new(label, view, prompt, kind);

    // The catalog — the 10 Studio areas, grouped Make / Talk / Manage. Content is data; edit here to add/refine an area.
    public static readonly IReadOnlyList<HomeCapability> Capabilities = new List<HomeCapability>
    {
        // ---- Make ----
        new("create", "make", "Create", "✨", "Generate images, video, music, or edits from a text prompt.",
            Need(mode: "media"), new[]
            {
                S("a neon dragon over a rainy city at night", "create", "a neon dragon over a rainy city at night", "image"),
                S("a 5-second clip of waves at sunset", "create", "waves crashing on a beach at sunset", "video"),
                S("an upbeat synthwave track", "create", "an upbeat retro synthwave track, 120 bpm", "music"),
            }),
        new("director", "make", "Director", "\U0001F3AC", "Turn a script or idea into an ordered shot list, then generate the shots.",
            Need(mode: "agent"), new[]
            {
                S("storyboard a 6-shot product teaser", "director", "a 6-shot teaser for a sleek smart-watch"),
            }),
        new("flow", "make", "Flow", "\U0001FAA2", "Chain steps into a node graph and run them in order.",
            Need(mode: "media"), new[] { S("open the node canvas", "flow") }),
        new("scene", "make", "Scene", "\U0001FA84", "Compose a base scene with isolated per-character regions in a single render.",
            Need(mode: "media"), new[] { S("compose a two-character scene", "scene") }),

        // ---- Talk ----
        new("chat", "talk", "Chat", "\U0001F4AC", "Talk to your local uncensored assistant — with tools, vision, documents, and long-term memory.",
            Need(mode: "agent"), new[]
            {
                S("brainstorm ten project names", "chat", "brainstorm ten punchy names for a local AI art studio"),
                S("summarize a document you attach", "chat", "summarize the document I'm about to attach"),
            }),
        new("cast", "talk", "Cast", "\U0001F3AD", "Build and reuse character cards (personas) for chat and scenes.",
            Need(), new[] { S("create a character card", "cast") }),
        new("voice", "talk", "Voice", "\U0001F50A", "Turn text into speech with cloneable voices (Chatterbox).",
            Need(service: "tts"), new[] { S("read a line aloud", "voice", "Welcome to DokiDex.") }),

        // ---- Manage ----
        new("library", "manage", "Library", "\U0001F5BC️", "Browse, search, rate, and remix everything you've generated.",
            Need(), new[] { S("see your latest creations", "library") }),
        new("models", "manage", "Models", "\U0001F4E6", "Install, switch, and manage the local image / video / LLM models.",
            Need(), new[] { S("see installed models", "models") }),
        new("status", "manage", "Status", "\U0001F4CA", "Live service health, the GPU meter, and the agent / media mode switch.",
            Need(), new[] { S("check what's running", "status") }),
    };
}
