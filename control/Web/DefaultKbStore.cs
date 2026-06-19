using System.IO;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// The DEFAULT/GLOBAL KB (the v0.16 KB-library polish): a single GLOBAL settings entry naming the named KB that
// every NEW conversation auto-attaches to — so your main project docs are present in every fresh chat. A tiny
// one-line settings file <root>/kbs/_default.json -> { "kbId": "kb-..." }, NOT a bool on KbRecord, deliberately:
//   (1) "exactly one default" is a GLOBAL invariant — a bool on N records can drift to two defaults and forces a
//       read-modify-write across every record to move it; a single settings entry is atomic.
//   (2) ADDITIVE byte-for-byte: KbRecord's shape and every existing kbs/<id>.json stay untouched, so KbStore's
//       List/Load/Save and every existing test pass unchanged. The leading-underscore stem means KbStore.List's
//       `*.json` enumeration would pick it up — but its DTO has no Id, so the existing
//       `!IsNullOrWhiteSpace(k.Id)` filter (KbStore.cs:52) ALREADY drops it.
//
// null / missing _default.json = NO default (today's behavior). Get() RESOLVE-VALIDATEs: it returns null if the
// stored kbId no longer KbStore.Loads (the library was deleted), so a dangling default silently degrades to
// no-default rather than pointing new chats at an empty/missing scope — mirroring the DELETE /kbs graceful-empty
// rule. Set(null/"") clears (deletes the file); Set(kbId) validates KbStore.Load(kbId) != null first.
public sealed record DefaultKbRecord(string? KbId);

public static class DefaultKbStore
{
    private static string Dir => Path.Combine(RepoPaths.Root, "kbs");
    private static string FilePath => Path.Combine(Dir, "_default.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // The current default KB id, or null when none is set OR the stored id no longer resolves (deleted library).
    // RESOLVE-VALIDATE: a dangling default degrades to null so new chats fall back to the no-default path.
    public static string? Get()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var rec = JsonSerializer.Deserialize<DefaultKbRecord>(File.ReadAllText(FilePath), JsonOpts);
            var id = rec?.KbId;
            if (string.IsNullOrWhiteSpace(id)) return null;
            return KbStore.Load(id) is not null ? id : null;   // dangling default -> null (graceful degrade)
        }
        catch { return null; }
    }

    // Set the default to a real, loadable KB id; "" / null CLEARS it (deletes the file). Returns false if the id
    // doesn't validate (KbStore.Load(kbId) == null) so a non-existent / unsafe id can't become the default.
    public static bool Set(string? kbId)
    {
        var id = (kbId ?? "").Trim();
        try
        {
            if (id.Length == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return true;
            }
            if (KbStore.Load(id) is null) return false;   // must name a real, loadable named KB
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new DefaultKbRecord(id)));
            return true;
        }
        catch { return false; }
    }
}
