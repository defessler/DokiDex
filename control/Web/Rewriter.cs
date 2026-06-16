using System.Text.RegularExpressions;

namespace DokiDex.Web;

// A browser request to steer-rewrite a prompt by a free-text instruction.
public sealed record RewriteRequest(string? Prompt, string? Instruction);

// The steerable rewriter: take the current prompt + a free-text instruction ("make it night", "darker tone",
// "add a red scarf") and return a revised prompt from the local instruct model. This is the directable,
// uncensored-tone-steering counterpart to the always-on :8013 auto-rewriter — here the USER says how.
// Used conversationally: the returned prompt becomes the composer's prompt, so the next instruction iterates
// on it (the prompt itself is the carried state — no session needed).
//
// The fragile part — stripping an LLM's chatty wrapping down to the bare prompt — is the pure, unit-tested
// CleanRewrite; the call degrades gracefully when the LLM is down (via LocalLlm).
public static class Rewriter
{
    public sealed record Result(bool Ok, string Prompt, string? Error);

    public static async Task<Result> RewriteAsync(string prompt, string instruction, CancellationToken ct)
    {
        prompt = (prompt ?? "").Trim();
        instruction = (instruction ?? "").Trim();
        if (prompt.Length == 0) return new Result(false, "", "empty prompt");
        if (instruction.Length == 0) return new Result(false, "", "empty instruction");

        const string sys = "You rewrite image-generation prompts. Apply the user's instruction to their prompt "
            + "and output ONLY the single revised prompt — no preamble, no commentary, no quotes, no code fence. "
            + "Keep what the instruction doesn't change.";
        var user = $"Prompt:\n{prompt}\n\nInstruction: {instruction}";

        var chat = await LocalLlm.ChatAsync(sys, user, temperature: 0.7, maxTokens: 512, ct).ConfigureAwait(false);
        if (!chat.Ok) return new Result(false, "", chat.Error);

        var cleaned = CleanRewrite(chat.Text);
        return cleaned.Length == 0
            ? new Result(false, "", "the model returned an empty rewrite — try again")
            : new Result(true, cleaned, null);
    }

    private static readonly Regex Fence = new(@"^```[a-zA-Z]*\s*|\s*```$", RegexOptions.Compiled);
    private static readonly Regex Preamble = new(
        @"^\s*(sure|okay|ok|certainly|here(?:'s| is| you go)?|the rewritten prompt|revised prompt|rewritten)\b[^:\n]*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pure: strip an LLM reply down to the bare prompt — drop ``` fences, a leading "Here is the prompt:"-style
    // preamble, and surrounding quotes; collapse blank lines. Total + side-effect-free -> unit-tested.
    public static string CleanRewrite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();
        // preamble FIRST: a "Here is the prompt:" line often precedes the fence, so dropping it can reveal the
        // opening ``` for the fence pass to strip.
        s = Preamble.Replace(s, "");              // "Here is the rewritten prompt:" -> ""
        s = s.Trim();
        s = Fence.Replace(s, "");                 // ```lang ... ``` -> inner
        s = s.Trim();
        s = StripWrappingQuotes(s);
        // collapse 3+ newlines to a single blank line; trim each end
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s;
    }

    private static string StripWrappingQuotes(string s)
    {
        if (s.Length < 2) return s;
        char a = s[0], b = s[^1];
        bool pair = (a == '"' && b == '"') || (a == '\'' && b == '\'')
                 || (a == '“' && b == '”') || (a == '‘' && b == '’');
        return pair ? s[1..^1].Trim() : s;
    }
}
