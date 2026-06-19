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
    public static List<object> Build(PersonaCard? card, IReadOnlyList<ChatTurn> history, string userMessage,
        int historyTurnBudget, IReadOnlyList<LoreEntry>? activeLore = null)
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

        foreach (var t in RecentTurns(history, historyTurnBudget))
            msgs.Add(new { role = NormalizeRole(t.Role), content = t.Content.Trim() });

        msgs.Add(new { role = "user", content = userMessage ?? "" });
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
