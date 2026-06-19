using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DokiDex.Web;

// A persona "character card" (the GPTs analog, local + uncensored). Stored as personas/<name>.json by Persona.cs.
//   System    = the character's behavior/voice (uncensored system prompt; no filter beyond the loaded model's)
//   Persona   = the user's own identity block (SillyTavern {{user}})
//   Greeting  = auto-seeds a fresh thread (used by the orchestrator/SPA, not folded into the prompt bundle)
//   Examples  = few-shot dialogue folded into the system bundle
//   Tier      = a speed/quality LlmTiers tier name
//   Voice     = a Tts.Voices() name for readback
//   Lorebook  = an attached lorebook name (P3; carried but unused in P0)
public sealed record PersonaCard(
    string? Name, string? Avatar, string? System, string? Persona, string? Greeting,
    string? Examples, string? Tier, string? Voice, string? Lorebook);

// One persisted turn in a conversation (Ts = ISO timestamp, optional / null in pure tests).
public sealed record ChatTurn(string Role, string Content, string? Ts);

// One retrieved knowledge-base chunk for context-injection (the KB's analog of a LoreEntry). Source = the doc
// filename the chunk came from (labels the chunk in the injected block); Content = the chunk text; Score = the
// cosine relevance (descending). Produced by the doc_index.py `doc_search` retrieval and rendered as ONE
// bounded "[Documents]" system turn by ChatPrompt.Build — never a 5th chat tool (context-injection, like the
// Lorebook, is unconditional, costs zero agent hops, and works on the non-tool send/stream paths too).
public sealed record DocChunk(string Source, string Content, double Score);

// The pure, unit-tested assembly of what reaches llama-swap: a persona system bundle + a most-recent-wins
// trimmed history + the new user turn, as an OpenAI message[]. This is the single source of truth for the
// prompt; the live LLM call (LocalLlm.ChatTurnsAsync) is a thin wrapper over the array this produces.
// Total + side-effect-free (mirrors Director.ParseShotlist / Vision.ParseVerdict discipline).
public static class ChatPrompt
{
    // A built-in uncensored, terse, studio-aware persona so raw chat works with zero setup (no card selected).
    public const string DefaultSystem =
        "You are the DokiGen Studio assistant — a local, uncensored, single-user creative AI. "
        + "Be direct, concise, and helpful. You run on the user's own machine; there is no content filter "
        + "beyond your own model. Help with anything the user asks, including creative and adult content.";

    // Build the OpenAI message[]: system bundle -> [World Info] lore (when any) -> trimmed history
    // (chronological, most-recent-wins) -> user turn. historyTurnBudget bounds how many prior turns ride along
    // (caps max_tokens growth); <= 0 drops all history. activeLore is the P3 keyword-triggered "World Info"
    // injection (already activated by Lorebook.Activate); a null/empty list preserves the EXACT prior output.
    //
    // P5 vision-in-chat: when imageDataUrl is supplied (non-empty), the NEW user turn's content becomes the OpenAI
    // multimodal content ARRAY — { type="text", text } + { type="image_url", image_url={ url } } — EXACTLY the
    // shape Vision.cs/LocalLlm.ChatVisionAsync send (same llama-swap vision path). When null/empty the user turn
    // stays the plain-string content, preserving the EXACT pre-P5 output. History turns are always plain strings.
    public static List<object> Build(PersonaCard? card, IReadOnlyList<ChatTurn> history, string userMessage,
        int historyTurnBudget, IReadOnlyList<LoreEntry>? activeLore = null, string? imageDataUrl = null,
        IReadOnlyList<DocChunk>? activeDocs = null)
    {
        var msgs = new List<object> { new { role = "system", content = SystemBundle(card) } };

        // P3: one [World Info] system turn placed AFTER the card bundle and BEFORE history (only when non-empty).
        if (activeLore is { Count: > 0 })
        {
            var lore = string.Join("\n", activeLore
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Content))
                .Select(e => e.Content!.Trim()));
            if (lore.Length > 0)
                msgs.Add(new { role = "system", content = "[World Info]\n" + lore });
        }

        // KB: one bounded [Documents] system turn — the sibling of [World Info] — placed right after it and still
        // BEFORE history (only when the conversation has retrieved docs). A null/empty list adds nothing, so the
        // no-KB output is byte-for-byte the prior output. DocsBlock applies the (DocsMaxChunks, DocsMaxChars) cap.
        var docs = DocsBlock(activeDocs);
        if (docs.Length > 0)
            msgs.Add(new { role = "system", content = "[Documents]\n" + docs });

        foreach (var t in RecentTurns(history, historyTurnBudget))
            msgs.Add(new { role = NormalizeRole(t.Role), content = t.Content.Trim() });

        var text = userMessage ?? "";
        if (string.IsNullOrWhiteSpace(imageDataUrl))
            msgs.Add(new { role = "user", content = text });
        else
            msgs.Add(new { role = "user", content = new object[]
            {
                new { type = "text", text },
                new { type = "image_url", image_url = new { url = imageDataUrl } },
            }});
        return msgs;
    }

    // The single source of truth for the "recent turns within budget" window: keep only non-empty turns, then take
    // the most-recent `budget` of them, in chronological order. Used by BOTH Build's history trim AND
    // Chat.ActivateLore's keyword-scan window, so a lore entry can never fire on a turn the prompt then drops.
    // budget <= 0 (or empty/all-blank history) yields an empty list. Total + side-effect-free.
    public static IReadOnlyList<ChatTurn> RecentTurns(IReadOnlyList<ChatTurn> history, int budget)
    {
        if (history is not { Count: > 0 } || budget <= 0) return System.Array.Empty<ChatTurn>();
        var kept = history
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Content))
            .ToList();
        if (kept.Count > budget)
            kept = kept.Skip(kept.Count - budget).ToList();
        return kept;
    }

    // KB context-block caps (the precedent is Chat's LoreMaxEntries/LoreMaxChars): at most this many retrieved
    // chunks, and a cumulative char budget so the injected block can't blow past max_tokens across turns.
    public const int DocsMaxChunks = 5;
    public const int DocsMaxChars = 4000;

    // A pathological source name can't dominate the budget: the "## <source>\n" label is capped to this many
    // chars of the source before the budget accounting (the ellipsis-truncated body still carries the content).
    private const int DocsMaxSourceChars = 120;

    // PURE + bounded: render the top retrieved chunks into the "[Documents]" block body, top-K capped to
    // DocsMaxChunks and the cumulative text capped to DocsMaxChars. Each chunk is labelled with its source
    // filename. A null/empty list yields "" (so Build injects nothing — the no-KB path is byte-for-byte). Total +
    // side-effect-free, so the budget can't silently drift — the same discipline the [World Info] block follows.
    //
    // The DocsMaxChars budget is HONEST: it counts the per-chunk "## <source>\n" header AND the inter-chunk
    // newline, not just the chunk CONTENT — so the emitted block is genuinely <= DocsMaxChars even with a long
    // source name (the prior `used += body.Length`-only accounting could overshoot by ~1KB).
    public static string DocsBlock(IReadOnlyList<DocChunk>? docs)
    {
        if (docs is not { Count: > 0 }) return "";
        var sb = new StringBuilder();
        var used = 0;
        var shown = 0;
        foreach (var d in docs)
        {
            if (d is null || string.IsNullOrWhiteSpace(d.Content)) continue;
            if (shown >= DocsMaxChunks || used >= DocsMaxChars) break;

            // Fixed (non-body) cost of emitting this chunk, charged against the SAME budget as the content: the
            // inter-chunk newline (every chunk after the first) + the "## <source>\n" header (when a source exists).
            var src = string.IsNullOrWhiteSpace(d.Source) ? "" : d.Source.Trim();
            if (src.Length > DocsMaxSourceChars) src = src[..DocsMaxSourceChars];
            var overhead = (sb.Length > 0 ? 1 : 0) + (src.Length > 0 ? src.Length + 4 : 0);   // "## " + name + "\n"

            // No room even for this chunk's header+newline (let alone any body) => stop cleanly.
            if (used + overhead >= DocsMaxChars) break;
            var bodyBudget = DocsMaxChars - used - overhead;

            var body = d.Content.Trim();
            // Truncate so body + the "…" sentinel together stay within bodyBudget (reserve the 1 ellipsis char),
            // keeping the WHOLE emitted block genuinely <= DocsMaxChars (no +1 ellipsis overshoot).
            if (body.Length > bodyBudget) body = body[..(bodyBudget - 1)] + "…";

            if (sb.Length > 0) sb.Append('\n');
            if (src.Length > 0) sb.Append("## ").Append(src).Append('\n');
            sb.Append(body);
            used += overhead + body.Length;
            shown++;
        }
        return sb.ToString();
    }

    // Compose the system turn from the card's behavior + the user-identity block + few-shot examples. Falls
    // back to the built-in default when no card / no system is provided, so chat always has a real persona.
    private static string SystemBundle(PersonaCard? card)
    {
        var sb = new StringBuilder();
        var system = string.IsNullOrWhiteSpace(card?.System) ? DefaultSystem : card!.System!.Trim();
        sb.Append(system);

        if (!string.IsNullOrWhiteSpace(card?.Persona))
            sb.Append("\n\n# About the user\n").Append(card!.Persona!.Trim());

        if (!string.IsNullOrWhiteSpace(card?.Examples))
            sb.Append("\n\n# Example dialogue\n").Append(card!.Examples!.Trim());

        return sb.ToString();
    }

    // Only "user" / "assistant" / "system" are valid OpenAI roles; anything else is treated as a user turn.
    private static string NormalizeRole(string? role) => (role ?? "").Trim().ToLowerInvariant() switch
    {
        "assistant" => "assistant",
        "system" => "system",
        _ => "user",
    };
}
