using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DokiDex.Web;

// A browser request to batch-generate from pasted/uploaded CSV.
public sealed record BatchRequest(string? Csv);

// A tiny, correct CSV parser for batch generation (paste/upload rows of per-gen params). Handles the parts
// that actually bite — quoted fields, commas + newlines inside quotes, and "" escaped quotes — then maps the
// header row to each data row as a dictionary. Pure + total -> unit-tested; the batch endpoint turns each row
// into a GenSubmit. (A prompt with a comma is the common case, so quoting MUST work.)
public static class Csv
{
    // Parse CSV text into rows of cells (RFC-4180-ish: " quotes a field, "" is a literal quote, CR/LF outside
    // quotes ends a row). Trailing blank line ignored.
    public static List<List<string>> Parse(string? text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return rows;
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }   // "" -> "
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { /* swallow; \n handles the row break */ }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else cell.Append(c);
        }
        // flush the final cell/row if the text didn't end with a newline
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // Parse with the first row as headers -> a dictionary per data row (header -> cell, case-insensitive keys;
    // short rows leave missing columns absent). Blank rows are skipped.
    public static List<Dictionary<string, string>> ParseWithHeader(string? text)
    {
        var rows = Parse(text);
        var result = new List<Dictionary<string, string>>();
        if (rows.Count < 2) return result;
        var headers = rows[0].Select(h => h.Trim()).ToList();
        for (int r = 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (cells.Count == 1 && string.IsNullOrWhiteSpace(cells[0])) continue;   // blank line
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Count && c < cells.Count; c++)
                if (headers[c].Length > 0) dict[headers[c]] = cells[c];
            result.Add(dict);
        }
        return result;
    }
}
