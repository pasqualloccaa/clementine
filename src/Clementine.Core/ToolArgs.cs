using System.Globalization;
using System.Text.Json;
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

    /// <summary>"1080" (lato lungo), "1920x1080" (dentro quel riquadro), "50%" → geometria di ImageMagick; le proporzioni restano.</summary>
    public static string? Resize(string text)
    {
        var m = ResizePattern().Match(text.Trim().Replace(" ", ""));
        if (!m.Success) return null;
        var (a, b) = (int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : -1);
        if (m.Groups[3].Success) return a is > 0 and <= 1000 ? $"{a}%" : null;
        if (b < 0) b = a;
        return a is > 0 and <= 30000 && b is > 0 and <= 30000 ? $"{a}x{b}" : null;
    }

    /// <summary>"0.1,0.2,0.5,0.4" → riquadro (x, y, larghezza, altezza) in frazioni dell'immagine, così vale per foto di ogni misura.</summary>
    public static (double X, double Y, double W, double H)? Rect(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 4) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0 || v[i] > 1) return null;
        return v[2] > 0 && v[3] > 0 && v[0] + v[2] <= 1.0001 && v[1] + v[3] <= 1.0001 ? (v[0], v[1], v[2], v[3]) : null;
    }

    public static string Rect(double x, double y, double w, double h) => FormattableString.Invariant($"{x:0.####},{y:0.####},{w:0.####},{h:0.####}");

    /// <summary>Le annotazioni scritte dall'editor (JSON); null se non valide.</summary>
    public static IReadOnlyList<Mark>? Marks(string text)
    {
        try { return JsonSerializer.Deserialize<List<Mark>>(text) is { Count: > 0 } marks && marks.All(m => m.IsValid) ? marks : null; }
        catch (JsonException) { return null; }
    }

    public static string Marks(IEnumerable<Mark> marks) => JsonSerializer.Serialize(marks);

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(mb|m|kb|k)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^(\d{1,5})(?:[x×X\*](\d{1,5}))?(%)?$")]
    private static partial Regex ResizePattern();
}

/// <summary>
/// Un segno di "Annota…", in frazioni dell'immagine: freccia da (X1,Y1) a (X2,Y2); rettangolo e "oscura" tra i due angoli;
/// testo con l'angolo in alto a sinistra in (X1,Y1). Spessori e caratteri sono in proporzione al lato corto,
/// così l'anteprima dell'editor e il file salvato coincidono.
/// </summary>
public sealed record Mark(string Kind, double X1, double Y1, double X2, double Y2, string Color = "#E53935", string? Text = null)
{
    public const string Arrow = "arrow", Box = "box", Redact = "redact", Label = "text";

    public bool IsValid => (Kind is Arrow or Box or Redact || Kind == Label && !string.IsNullOrWhiteSpace(Text))
        && new[] { X1, Y1, X2, Y2 }.All(v => v is >= 0 and <= 1) && Color.Length == 7 && Color[0] == '#';

    public static double StrokeWidth(double shortSide) => Math.Max(2, shortSide * 0.007);
    public static double FontSize(double shortSide) => Math.Max(12, shortSide * 0.05);

    /// <summary>I tre vertici della punta di una freccia che finisce in (x2,y2).</summary>
    public static (double X, double Y)[] ArrowHead(double x1, double y1, double x2, double y2, double stroke)
    {
        var length = Math.Max(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)), 1e-6);
        var (ux, uy) = ((x2 - x1) / length, (y2 - y1) / length);
        var (back, side) = (stroke * 5, stroke * 3);
        return [(x2, y2), (x2 - ux * back - uy * side, y2 - uy * back + ux * side), (x2 - ux * back + uy * side, y2 - uy * back - ux * side)];
    }
}
