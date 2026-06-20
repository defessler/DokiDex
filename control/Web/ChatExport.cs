using System;
using System.Text;

namespace DokiDex.Web;

// PURE conversation -> portable markdown formatter: a persona/lorebook/KB header + each stored turn, rendered for
// a downloaded .md (the export endpoint is a thin Results.File over this). Total + side-effect-free (no disk, no
// model), so it's unit-tested with a hand-built Conversation — mirroring ChatPrompt.Build / Director.ParseShotlist
// discipline. Read-only over the on-disk shape (Conversation/ChatTurn); nothing on the persist path is touched.
//
// Turn-type handling (the record only stores user/assistant ChatTurns — no system, no separate tool role on disk):
//   • System context (persona/lorebook/KB) is NOT per-turn — it's synthesized at send time by ChatPrompt.Build
//     and never saved — so it goes in the HEADER, and there is nothing to exclude per-turn.
//   • A tools round is just the final assistant Content that Chat.AgentAsync saved (intermediate tool steps are
//     UI-only chips, never in ChatTurn) — so the assistant answer exports verbatim, nothing extra to do.
//   • An image attachment rides a single send and is NOT stored in the thread; defensively, if a turn's Content
//     contains a data: URL it's summarized rather than dumping base64.
//   • The assistant may emit raw markdown/HTML; this is a downloaded file (not injected into the DOM), so Content
//     is emitted VERBATIM (no escaping) so the user's own markdown renders — but bounded (the caps below).
public static class ChatExport
{
    // Bounds for a pathological huge thread, all const so they're asserted in tests:
    //   • per-turn Content cap (longer => truncated with a marker),
    //   • total turns rendered (keep the most RECENT, matching the most-recent-wins history bias),
    //   • an overall byte cap as a final backstop.
    public const int MaxTurnChars = 20_000;
    public const int MaxTurns = 2_000;
    public const int MaxBytes = 8 * 1024 * 1024;

    public static string ToMarkdown(Conversation conv)
    {
        if (conv is null) return "";
        var msgs = conv.Messages ?? Array.Empty<ChatTurn>();

        var sb = new StringBuilder();
        sb.Append("# Conversation ").Append(conv.Id).Append('\n');
        sb.Append("- Persona: ").Append(string.IsNullOrWhiteSpace(conv.Persona) ? "default" : conv.Persona).Append('\n');
        sb.Append("- Lorebook: ").Append(string.IsNullOrWhiteSpace(conv.Lorebook) ? "none" : conv.Lorebook).Append('\n');
        sb.Append("- Knowledge base: ").Append(string.IsNullOrWhiteSpace(conv.KbId) ? "none" : conv.KbId).Append('\n');
        sb.Append("- Created: ").Append(conv.Created).Append('\n');
        sb.Append("- Turns: ").Append(msgs.Count).Append('\n');
        sb.Append('\n').Append("---").Append('\n');

        // Keep only the most-recent MaxTurns; note how many earlier turns were dropped (most-recent-wins).
        var start = 0;
        if (msgs.Count > MaxTurns)
        {
            start = msgs.Count - MaxTurns;
            sb.Append('\n').Append("… [").Append(start).Append(" earlier turns omitted]").Append('\n');
        }

        // Incremental UTF-8 byte counter for the cap backstop: seed it with everything emitted so far (header +
        // any omitted-turns note), then add ONLY each freshly appended chunk per turn instead of re-scanning the
        // whole growing StringBuilder every iteration — O(n) total rather than O(n²). The running total mirrors
        // Encoding.UTF8.GetByteCount(sb.ToString()) exactly at each check, so the break decision (and therefore the
        // emitted bytes) is byte-for-byte identical to the prior whole-buffer scan.
        var byteLen = Encoding.UTF8.GetByteCount(sb.ToString());

        for (var i = start; i < msgs.Count; i++)
        {
            var t = msgs[i];
            if (t is null) continue;
            var label = "\n" + Label(t.Role) + "\n";
            var body = RenderContent(t.Content) + "\n";
            sb.Append(label).Append(body);
            byteLen += Encoding.UTF8.GetByteCount(label) + Encoding.UTF8.GetByteCount(body);

            // Byte-cap backstop: if we've crossed the cap, stop cleanly with a marker (the most-recent bias means
            // we keep what's been emitted, which is the earliest of the kept window first — acceptable backstop).
            if (byteLen >= MaxBytes)
            {
                sb.Append('\n').Append("… [output truncated at byte cap]").Append('\n');
                break;
            }
        }

        var outp = sb.ToString();
        // Final hard backstop in the (defensive) event a single header/turn still overshot the byte cap.
        if (Encoding.UTF8.GetByteCount(outp) > MaxBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(outp);
            // Trim to the cap on a char boundary (UTF8.GetString tolerates a clipped trailing sequence by replacing it).
            outp = Encoding.UTF8.GetString(bytes, 0, MaxBytes);
        }
        return outp;
    }

    // Map a stored role to its markdown label, using the SAME user/assistant mapping the SPA uses (anything !=
    // "assistant" is "user", index.html:1818). A defensively-unknown future role is rendered as a blockquote tag
    // rather than mislabeled as You/Assistant.
    private static string Label(string? role)
    {
        var r = (role ?? "").Trim().ToLowerInvariant();
        return r switch
        {
            "assistant" => "**Assistant:**",
            "user" or "" => "**You:**",
            _ => "> [" + r + "]",
        };
    }

    // Emit Content verbatim (it's a downloaded file, not DOM-injected), but: summarize a data: URL attachment
    // instead of dumping its base64, and cap a pathological huge turn with a marker.
    private static string RenderContent(string? content)
    {
        var c = content ?? "";
        if (c.Contains("data:", StringComparison.OrdinalIgnoreCase) &&
            c.Contains("base64,", StringComparison.OrdinalIgnoreCase))
            return "_[image attachment omitted]_";

        if (c.Length > MaxTurnChars)
        {
            var extra = c.Length - MaxTurnChars;
            return c[..MaxTurnChars] + "… [truncated, " + extra + " more chars]";
        }
        return c;
    }
}
