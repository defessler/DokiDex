using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A pending image-gen request queued FROM CHAT, or (1.2) from Create's own "Queue for later" choice. A chat turn
// runs with the coder LLM resident on the single 32GB GPU; SwarmUI (the renderer) is GPU-exclusive with it (26GB
// LLM + 18GB SwarmUI > 32GB — decisions.md), so a gen CANNOT render mid-conversation and the chat model must NOT
// be evicted mid-turn. The generate_image tool's job is therefore QUEUE-AND-NOTIFY: persist the request under
// RepoPaths.Root and tell the user to switch to Media mode. This record is the durable, small subset of GenSubmit
// that survives the eventual GPU flip. Conversation links it back to the chat thread for later surfacing
// (null for a Create-originated queue item — see PendingGenSubmit). Id/Created are SERVER-set — no client path => no traversal.
public sealed record PendingGen(
    string Id, string Prompt, string Kind, string? Model, int Count, string Created, string? Conversation,
    string? Status = null, string? ResultRel = null, string? Error = null,
    string? InitImage = null, double? Strength = null, string? Preview = null);
    // lifecycle: queued -> rendering -> done/failed; ResultRel = finished media (gallery-relative); Error on failure.
    // InitImage/Strength = edit/img2img source (the edit_image tool). Preview = in-flight data-URL while rendering
    // (the ChatGenCoordinator streams it so the chat thread can show a warming-up preview before the final image).

// POST /api/pending-gen body (1.2, "Queue for later" from Create/Director — see StudioHost + queueCurrentGen/
// queueShot in index.html). Deliberately the SAME reduced field set the chat generate_image tool already
// persists — prompt/kind/model/count — NOT the full GenSubmit (no ControlNet/LoRA/aspect/seed/camera/etc; the
// Create "Switch & run" choice is the full-fidelity path for those) and never an init image at this seam (a
// fresh browser upload is a data: URL with no durable Library-relative home until the deferred render runs — see
// PendingGenStore.NormalizeSubmit and the JS-side comment on why generate() only offers Queue for later without one).
public sealed record PendingGenSubmit(string Prompt, string? Kind = "image", string? Model = null, int Count = 1);

// Pending-gen store — file-based under RepoPaths.Root/pending-gen/<id>.json (the durable install/repo root that
// survives a mode-switch), mirroring ChatStore exactly (JSON via the same serializer, graceful try/catch). The
// file STEM is a server-generated id (timestamp + short guid), so there is no user-supplied path; RecipeStore.SafeName
// still guards Load/Delete defensively against a malformed id.
// This is the durable side the chat tool writes; the existing media pipeline renders it after the user switches.
public static class PendingGenStore
{
    private static string Dir => Path.Combine(RepoPaths.Root, "pending-gen");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // PURE: normalize a POST /api/pending-gen body (1.2's "Queue for later") the same way ChatTools.MapGenArgs
    // normalizes a chat tool call — trimmed prompt, lower-cased kind (blank -> "image"). UNLIKE MapGenArgs this
    // does NOT restrict kind to the image family: a Create-queued submit is an explicit human choice from the
    // composer, not an LLM's guess, so video/music/i2v/foley pass through (the deferred render just gets fewer
    // of the recipe's extra fields than a full Switch & run would carry). Model trimmed-or-null; count clamped to
    // [1,9] (matches /api/generate's clamp). A null body degrades to safe defaults rather than throwing. Total +
    // side-effect-free => the endpoint's test seam (no disk/HTTP).
    public static (string prompt, string kind, string? model, int count) NormalizeSubmit(PendingGenSubmit? body)
    {
        var prompt = (body?.Prompt ?? "").Trim();
        var kind = string.IsNullOrWhiteSpace(body?.Kind) ? "image" : body!.Kind!.Trim().ToLowerInvariant();
        var model = string.IsNullOrWhiteSpace(body?.Model) ? null : body!.Model!.Trim();
        var count = Math.Clamp(body?.Count ?? 1, 1, 9);
        return (prompt, kind, model, count);
    }

    // Append a pending gen with a server-generated, traversal-free id (timestamp + short guid, all SafeName-legal),
    // writing its JSON file and returning the record. Graceful: a disk hiccup still returns the in-memory record
    // (the caller's notice is honest about "queued"), the agent loop never crashes on the tool.
    public static PendingGen Enqueue(string prompt, string kind, string? model, int count, string? conversation,
                                     string? initImage = null, double? strength = null)
    {
        var rec = new PendingGen(
            Id: $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            Prompt: prompt,
            Kind: kind,
            Model: model,
            Count: count,
            Created: DateTime.UtcNow.ToString("o"),
            Conversation: conversation,
            Status: "queued",
            InitImage: initImage,
            Strength: strength);
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, rec.Id + ".json"), JsonSerializer.Serialize(rec)); }
        catch { }
        return rec;
    }

    // Newest-first listing, graceful try/catch per file (a corrupt entry is skipped, never throws) — mirrors
    // ChatStore.List. Ordered by the Created timestamp string descending.
    public static IReadOnlyList<PendingGen> List()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<PendingGen>();
        var outp = new List<PendingGen>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            try
            {
                var p = JsonSerializer.Deserialize<PendingGen>(File.ReadAllText(f), JsonOpts);
                if (p is not null && !string.IsNullOrWhiteSpace(p.Id)) outp.Add(p);
            }
            catch { }
        return outp.OrderByDescending(p => p.Created, StringComparer.Ordinal).ToList();
    }

    // PURE: the coordinator's drain order — queued items, OLDEST first (FIFO) so an earlier-queued chat renders
    // first. Single source of truth for "what renders next", unit-tested with no disk/GPU.
    public static IReadOnlyList<PendingGen> FilterQueued(IEnumerable<PendingGen> all)
        => all.Where(p => string.Equals(p.Status, "queued", StringComparison.OrdinalIgnoreCase))
              .OrderBy(p => p.Created, StringComparer.Ordinal).ToList();

    // The queued chat-gens awaiting render, oldest-first — the ChatGenCoordinator's work queue.
    public static IReadOnlyList<PendingGen> Queued() => FilterQueued(List());

    public static PendingGen? Load(string? id)
    {
        var n = RecipeStore.SafeName(id);
        if (n is null) return null;
        var p = Path.Combine(Dir, n + ".json");
        try { return File.Exists(p) ? JsonSerializer.Deserialize<PendingGen>(File.ReadAllText(p), JsonOpts) : null; }
        catch { return null; }
    }

    // Re-persist a pending gen with a new lifecycle status (+ optional result path / error), preserving everything
    // else (id, prompt, the Conversation backlink). The deferred renderer drives queued -> rendering -> done/failed
    // so the chat SPA can poll /pending-gen and surface the finished media inline in its originating thread. Returns
    // the updated record, or null for an unknown/unsafe id (loaded via the SafeName-guarded Load). Graceful: a disk
    // hiccup returns the prior record rather than throwing — the renderer/agent paths must never crash on a write.
    public static PendingGen? SetStatus(string? id, string status, string? resultRel = null, string? error = null, string? preview = null)
    {
        var rec = Load(id);
        if (rec is null) return null;
        var updated = rec with { Status = status, ResultRel = resultRel, Error = error, Preview = preview };
        try { File.WriteAllText(Path.Combine(Dir, rec.Id + ".json"), JsonSerializer.Serialize(updated)); }
        catch { return rec; }
        return updated;
    }

    public static bool Delete(string? id)
    {
        var n = RecipeStore.SafeName(id);
        if (n is null) return false;
        var p = Path.Combine(Dir, n + ".json");
        try { if (File.Exists(p)) File.Delete(p); return true; }
        catch { return false; }
    }
}
