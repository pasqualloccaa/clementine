using System.Globalization;
using System.Text.RegularExpressions;

namespace Clementine.Core;

/// <summary>Lettura dei valori scritti a mano negli strumenti con parametri; null = non valido.</summary>
public static partial class ToolArgs
{
    /// <summary>"90", "1:30", "1:02:03", "1:30,5" → secondi.</summary>
    public static double? Seconds(string text)
    {
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return null;
        var total = 0.0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total;
    }

    /// <summary>"0:30-1:45", "30 - 105" → (inizio, fine) in secondi, con la fine dopo l'inizio.</summary>
    public static (double Start, double End)? TimeRange(string text)
    {
        var parts = text.Split('-');
        return parts.Length == 2 && Seconds(parts[0]) is { } start && Seconds(parts[1]) is { } end && end > start ? (start, end) : null;
    }

    /// <summary>"2 MB", "1,5mb", "500 KB", "800" (KB) → byte.</summary>
    public static long? Size(string text)
    {
        var m = SizePattern().Match(text.Trim().Replace(',', '.'));
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return null;
        var unit = m.Groups[2].Value.ToLowerInvariant() is "mb" or "m" ? 1024 * 1024 : 1024;
        return (long)(value * unit);
    }

    /// <summary>"3,1,2", "1-5, 8" → numeri di pagina (da 1) nell'ordine scritto; null se una è fuori da 1..count.</summary>
    public static int[]? Pages(string text, int count)
    {
        var pages = new List<int>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (range.Length > 2 || !int.TryParse(range[0], out var from) || !int.TryParse(range[^1], out var to)) return null;
            if (from < 1 || to < 1 || from > count || to > count) return null;
            pages.AddRange(from <= to ? Enumerable.Range(from, to - from + 1) : Enumerable.Range(to, from - to + 1).Reverse());
        }
        return pages.Count > 0 ? [.. pages] : null;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(mb|m|kb|k)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();
}
