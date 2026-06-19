using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// A persisted multi-turn conversation: the on-disk memory the one-shot LocalLlm structurally lacks. The id is
// SERVER-generated (never a client path => no traversal surface); Persona/Lorebook name the card + world-info
// the thread runs under; Messages is the append-on-turn transcript that reload restores.
// KbId (optional) names the per-conversation KNOWLEDGE BASE the thread runs over — the scope key for the
// doc_index.db chunks attached to this conversation (mirrors the Lorebook? field: carried in the same <id>.json,
// null on a thread with no attached docs so the no-KB chat path stays byte-for-byte). First slice: KbId == the
// conversation Id, so a doc attaches to a thread, not globally; a later slice can promote it to a named library.
public sealed record Conversation(
    string Id, string? Persona, string? Lorebook, string Created, IReadOnlyList<ChatTurn> Messages,
    string? KbId = null);

// Conversation store — file-based under <home>/chats/<id>.json, mirroring SavedSearches (JSON via the same
// serializer, graceful try/catch). Unlike personas/recipes the FILE STEM is a server-generated id, so there is
// no user-supplied path; SafeName still guards Load/Delete defensively against a malformed id from any caller.
public static class ChatStore
{
    private static string Dir => Path.Combine(RepoPaths.Root, "chats");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // A fresh conversation with a server-generated, traversal-free id (timestamp + short guid, all SafeName-legal).
    public static Conversation NewConversation(string? persona, string? lorebook)
        => new(
            Id: $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            Persona: persona,
            Lorebook: lorebook,
            Created: DateTime.UtcNow.ToString("o"),
            Messages: new List<ChatTurn>());

    public static IReadOnlyList<Conversation> List()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<Conversation>();
        var outp = new List<Conversation>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            try
            {
                var c = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(f), JsonOpts);
                if (c is not null && !string.IsNullOrWhiteSpace(c.Id)) outp.Add(c);
            }
            catch { }
        return outp.OrderByDescending(c => c.Created, StringComparer.Ordinal).ToList();
    }

    public static Conversation? Load(string? id)
    {
        var n = RecipeStore.SafeName(id);
        if (n is null) return null;
        var p = Path.Combine(Dir, n + ".json");
        try { return File.Exists(p) ? JsonSerializer.Deserialize<Conversation>(File.ReadAllText(p), JsonOpts) : null; }
        catch { return null; }
    }

    public static bool Save(Conversation? conv)
    {
        var n = RecipeStore.SafeName(conv?.Id);
        if (n is null) return false;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, n + ".json"), JsonSerializer.Serialize(conv)); return true; }
        catch { return false; }
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
