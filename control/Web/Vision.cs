using System.Text.RegularExpressions;

namespace DokiDex.Web;

public sealed record DescribeRequest(string? Name);
public sealed record VerifyRequest(string? Name, string? Prompt);

// A prompt-adherence verdict from output verification: did the image match its prompt, and why.
public sealed record VerifyVerdict(bool Pass, string Reason);

// Two VLM surfaces over the local multimodal model (llama-swap :8080), both GATED — they activate when the
// loaded model is vision-capable and degrade to a clear error otherwise (LocalLlm path):
//   • Describe       — reverse-prompt: an image -> an editable generation prompt (drops into the composer).
//   • Verify         — opt-in QA: does a finished image match its prompt? -> PASS/FAIL + a terse reason.
// The fragile part (turning a chatty model reply into a clean prompt / a structured verdict) is the pure,
// unit-tested half: CleanRewrite (reused) for Describe, ParseVerdict for Verify.
public static class Vision
{
    public static async Task<LocalLlm.ChatResult> DescribeAsync(string imageDataUrl, CancellationToken ct)
    {
        const string sys = "You caption images for an image-generation studio. Look at the image and write ONE vivid "
            + "generation prompt that would recreate it — subject, style, composition, lighting, palette. "
            + "Output ONLY the prompt: no preamble, no commentary, no quotes, no code fence.";
        var chat = await LocalLlm.ChatVisionAsync(sys, "Describe this image as a generation prompt.", imageDataUrl, 0.4, 400, ct, LlmTiers.Vision).ConfigureAwait(false);
        if (!chat.Ok) return chat;
        var cleaned = Rewriter.CleanRewrite(chat.Text);
        return cleaned.Length == 0
            ? new LocalLlm.ChatResult(false, "", "the model returned nothing — is a vision model loaded?")
            : new LocalLlm.ChatResult(true, cleaned, null);
    }

    public static async Task<(bool Ok, VerifyVerdict? Verdict, string? Error)> VerifyAsync(string imageDataUrl, string prompt, CancellationToken ct)
    {
        const string sys = "You verify whether a generated image matches its prompt for a QA pass. Reply with a single "
            + "line beginning 'PASS:' if the key subjects and requested attributes are present, or 'FAIL:' if "
            + "something important is missing or wrong, followed by a terse reason. Nothing else.";
        var chat = await LocalLlm.ChatVisionAsync(sys, $"Prompt: {prompt}\nDoes the image match the prompt?", imageDataUrl, 0.2, 200, ct, LlmTiers.Vision).ConfigureAwait(false);
        return chat.Ok ? (true, ParseVerdict(chat.Text), null) : (false, null, chat.Error);
    }

    private static readonly Regex Fail = new(@"\bfail(ed|ure|s)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Pass = new(@"\bpass(ed|es)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Label = new(@"^\s*(pass|fail)[a-z]*\s*[:\-—]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pure: turn the model's reply into a verdict. FAIL wins over PASS if both appear (the model usually leads
    // with the verdict word and elaborates the failure); ambiguous/empty replies are conservatively NOT a pass
    // (a QA triage should surface the uncertain ones). Reason = text after the PASS:/FAIL: label, first line.
    public static VerifyVerdict ParseVerdict(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return new VerifyVerdict(false, "no response");
        bool pass = Pass.IsMatch(s) && !Fail.IsMatch(s);
        var reason = Label.Replace(s, "").Trim();
        if (reason.Length == 0) reason = s;
        var nl = reason.IndexOf('\n');
        if (nl > 0) reason = reason[..nl].Trim();
        if (reason.Length > 300) reason = reason[..300].Trim();
        return new VerifyVerdict(pass, reason);
    }
}
