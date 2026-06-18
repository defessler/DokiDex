using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DokiDex.Web;

public sealed record PitchDeckRequest(string? Title, List<string>? Names, string? Tier = null);
public sealed record DeckScene(string Prompt, string ImageSrc, string Kind);
public sealed record DeckCast(string Name, string Profile);
public sealed record Deck(string Title, string Logline, string Synopsis, List<DeckScene> Scenes, List<DeckCast> Cast);

// One-click story-bible / pitch-deck export: gather a project's images + their prompts, have the local LLM
// write a logline + synopsis (GATED — degrades to image-only when the LLM is down), list the @-reference cast,
// and lay it all out as ONE self-contained, themed HTML file (images inlined as data URLs) the user can open
// offline or print to PDF. The fragile/structural halves are pure + unit-tested: ParseProse and BuildHtml.
public static class PitchDeck
{
    // Pure: parse the LLM reply into (logline, synopsis). Tolerant — labelled "LOGLINE:/SYNOPSIS:" preferred,
    // else first line = logline and the rest = synopsis. Any "logline:" echoed inside the synopsis is dropped.
    public static (string Logline, string Synopsis) ParseProse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return ("", "");
        string log = "", syn = "";
        var mLog = Regex.Match(s, @"logline\s*[:\-]\s*(.+)", RegexOptions.IgnoreCase);
        if (mLog.Success) log = mLog.Groups[1].Value.Trim();
        var mSyn = Regex.Match(s, @"synopsis\s*[:\-]\s*([\s\S]+)", RegexOptions.IgnoreCase);
        if (mSyn.Success) syn = mSyn.Groups[1].Value.Trim();
        if (log.Length == 0 && syn.Length == 0)
        {
            var parts = s.Split('\n', 2);
            log = parts[0].Trim();
            syn = parts.Length > 1 ? parts[1].Trim() : "";
        }
        var nl = log.IndexOf('\n'); if (nl > 0) log = log[..nl].Trim();
        // if synopsis came first and swept up a trailing "LOGLINE: …", trim that tail
        syn = Regex.Replace(syn, @"\n?\s*logline\s*[:\-].*$", "", RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();
        return (log, syn);
    }

    public static async Task<Deck> ComposeAsync(string? title, IReadOnlyList<DeckScene> scenes, IReadOnlyList<DeckCast> cast, CancellationToken ct, string? model = null)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "Untitled Project" : title.Trim();
        string logline = "", synopsis = "";
        if (scenes.Count > 0)
        {
            const string sys = "You are a story editor. From a list of scene prompts, infer the project and write a "
                + "one-sentence LOGLINE and a one-paragraph SYNOPSIS. Reply EXACTLY as two lines:\n"
                + "LOGLINE: <one sentence>\nSYNOPSIS: <one paragraph>";
            var user = "Scene prompts:\n" + string.Join("\n", scenes.Select((s, i) => $"{i + 1}. {s.Prompt}"));
            var chat = await LocalLlm.ChatAsync(sys, user, 0.7, 500, ct, model).ConfigureAwait(false);
            if (chat.Ok) (logline, synopsis) = ParseProse(chat.Text);
        }
        return new Deck(t, logline, synopsis, scenes.ToList(), cast.ToList());
    }

    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Pure: a self-contained themed HTML document (no external assets — images are inlined data URLs). Total.
    public static string BuildHtml(Deck d)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")
          .Append(Esc(d.Title)).Append(" — story bible</title><style>")
          .Append("*{box-sizing:border-box}body{margin:0;background:#0b0e14;color:#e6e9ef;font:15px/1.6 system-ui,Segoe UI,sans-serif;padding:48px}")
          .Append("h1{color:#f5c451;font-size:34px;margin:0 0 4px}h2{color:#54d1db;border-bottom:1px solid #232a36;padding-bottom:6px;margin:38px 0 14px}")
          .Append(".log{font-size:18px;color:#cfd6e4;font-style:italic;margin:0 0 24px}.syn{max-width:760px}")
          .Append(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:16px}")
          .Append(".sc{background:#121723;border:1px solid #232a36;border-radius:10px;overflow:hidden}")
          .Append(".sc img{width:100%;display:block;aspect-ratio:1;object-fit:cover}.sc .p{padding:9px 11px;font-size:12px;color:#aeb6c6}")
          .Append(".cast{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:12px}")
          .Append(".ca{background:#121723;border:1px solid #232a36;border-radius:10px;padding:12px 14px}.ca b{color:#f5c451}")
          .Append(".ft{margin-top:48px;color:#5b6678;font-size:12px}</style></head><body>");
        sb.Append("<h1>").Append(Esc(d.Title)).Append("</h1>");
        if (d.Logline.Length > 0) sb.Append("<p class=\"log\">").Append(Esc(d.Logline)).Append("</p>");
        if (d.Synopsis.Length > 0) sb.Append("<h2>Synopsis</h2><p class=\"syn\">").Append(Esc(d.Synopsis)).Append("</p>");
        if (d.Cast.Count > 0)
        {
            sb.Append("<h2>Cast</h2><div class=\"cast\">");
            foreach (var c in d.Cast)
                sb.Append("<div class=\"ca\"><b>").Append(Esc(c.Name)).Append("</b>")
                  .Append(c.Profile.Length > 0 ? "<div>" + Esc(c.Profile) + "</div>" : "").Append("</div>");
            sb.Append("</div>");
        }
        sb.Append("<h2>Scenes (").Append(d.Scenes.Count).Append(")</h2><div class=\"grid\">");
        foreach (var s in d.Scenes)
            sb.Append("<div class=\"sc\"><img src=\"").Append(s.ImageSrc).Append("\" alt=\"\"><div class=\"p\">")
              .Append(Esc(s.Prompt)).Append("</div></div>");
        sb.Append("</div><p class=\"ft\">Generated locally by DokiGen Studio — a personal story bible, fully offline.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
