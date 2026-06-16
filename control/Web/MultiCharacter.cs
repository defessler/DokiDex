using System.Text;
using System.Text.RegularExpressions;

namespace DokiDex.Web;

// One character in a multi-character scene: an isolated prompt + a coarse position (a cell on a 5x5 grid,
// 0..24, row-major; -1 = unplaced -> no region box).
public sealed record CharacterSpec(string Prompt, int Cell = -1);

// A multi-character scene to compose.
public sealed record MultiCharSpec(string? Base, List<CharacterSpec>? Characters, string? Relationship);

// The Multi-Character Directorial Composer's pure core: compile a base scene prompt + up to 6 isolated
// per-character prompts (each pinned to a coarse 5x5 grid cell) into ONE SwarmUI prompt string that uses
// regional <object:...> tags to keep each character's attributes from bleeding into the others — the
// missing primitive for an anime/furry multi-character studio.
//
// The compiled prompt rides the existing gen path in RAW mode (the <object:...> tags are processed by
// SwarmUI itself and must not be wrapped by the :8013 rewriter). The base holds the count tags (2girls,
// etc.); a leading count tag on a character prompt is stripped (it belongs in the base, not the region).
//
// The tag FORMAT is isolated in ObjectTag (SwarmUI's documented <object:prompt,x,y,w,h>); the placement
// LOGIC (cell -> fractional box, isolation, count-prefix stripping) is what the unit tests lock.
public static class MultiCharacter
{
    public const int Grid = 5;          // 5x5 coarse placement grid
    public const int MaxCharacters = 6; // bounded for 32GB VRAM (per the backlog)

    // a leading anime-style count tag (1girl / 2girls / 3boys / 1other ...) belongs in the BASE, not a
    // per-character region — strip it so the character prompt is pure attributes (anti-bleed).
    private static readonly Regex CountPrefix = new(@"^\s*\d+\s*(girls?|boys?|others?|people|men|women)\s*,?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Compile(MultiCharSpec spec)
    {
        var sb = new StringBuilder();
        var baseText = (spec.Base ?? "").Trim();
        if (baseText.Length > 0) sb.Append(baseText);

        foreach (var c in (spec.Characters ?? new()).Take(MaxCharacters))
        {
            var p = StripCount((c.Prompt ?? "").Trim());
            if (p.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            if (c.Cell is >= 0 && c.Cell < Grid * Grid)
            {
                var (x, y, w, h) = CellBox(c.Cell);
                sb.Append(ObjectTag(p, x, y, w, h));
            }
            else sb.Append(p);   // unplaced character: contributes its prompt without a region box
        }

        // the relationship line ("HERO hugs VILLAIN") is a single scene-level interaction phrase; it sits in
        // the base scene context (a real deployment routes it through the LLM rewriter first — kept literal here).
        var rel = (spec.Relationship ?? "").Trim();
        if (rel.Length > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append(rel); }

        return sb.ToString();
    }

    // grid cell (0..24, row-major) -> a fractional bounding box. A character is taller than wide, so the box
    // is the cell's column-width by ~2 cells tall, centered on the cell and clamped to [0,1].
    public static (double x, double y, double w, double h) CellBox(int cell)
    {
        int col = cell % Grid, row = cell / Grid;
        double cw = 1.0 / Grid;                 // cell width/height = 0.2
        double w = cw, h = cw * 2;               // portrait-ish region
        double cx = (col + 0.5) * cw, cy = (row + 0.5) * cw;   // cell center
        double x = Clamp01(cx - w / 2), y = Clamp01(cy - h / 2);
        if (x + w > 1) x = 1 - w;
        if (y + h > 1) y = 1 - h;
        return (Round(x), Round(y), Round(w), Round(h));
    }

    private static string StripCount(string s) => CountPrefix.Replace(s, "");
    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static double Round(double v) => Math.Round(v, 2);

    // SwarmUI regional bounding-box tag. The ONE format-coupled line — adjust here if SwarmUI's object
    // syntax differs; the placement logic + tests above stay correct.
    private static string ObjectTag(string prompt, double x, double y, double w, double h)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "<object:{0},{1},{2},{3},{4}>", prompt, x, y, w, h);
}
