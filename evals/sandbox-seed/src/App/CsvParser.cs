namespace App;

public static class CsvParser
{
    /// <summary>
    /// Parses a single CSV line into fields. Quoted fields are not supported yet.
    /// </summary>
    public static string[] ParseLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return Array.Empty<string>();
        return line.Split(',');
    }
}
