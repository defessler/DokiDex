using System.Text;

namespace App;

public static class ReportFormatter
{
    public static string FormatHeader(string? title)
    {
        // normalize: trim, collapse whitespace runs, strip control chars
        var sb = new StringBuilder();
        var lastWasSpace = false;
        foreach (var ch in (title ?? string.Empty).Trim())
        {
            if (char.IsControl(ch)) continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        var normalized = sb.ToString();
        return $"=== {normalized.ToUpperInvariant()} ===";
    }

    public static string FormatFooter(string? note)
    {
        // normalize: trim, collapse whitespace runs, strip control chars
        var sb = new StringBuilder();
        var lastWasSpace = false;
        foreach (var ch in (note ?? string.Empty).Trim())
        {
            if (char.IsControl(ch)) continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        var normalized = sb.ToString();
        return $"--- {normalized} ---";
    }
}
