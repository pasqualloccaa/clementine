namespace Clementine.Core;

/// <summary>Mappa formati (CLAUDE.md, parità con Tangerine) e conversioni proposte dalla ruota.</summary>
public static class Formats
{
    // Documenti/presentazioni/fogli richiedono LibreOffice: senza, quel motore non li accetta. PDF → Word non lo usa.
    // Verificato su LibreOffice 26.8: niente EPUB in ingresso. PDF → pptx/epub/xlsx tolti: escono caselle di testo, non documenti.
    static readonly Dictionary<FileKind, (string[] In, string[] Out)> Map = new()
    {
        [FileKind.Document] = (["docx", "doc", "odt", "rtf", "txt", "md", "pages"], ["docx", "odt", "pdf", "txt", "md", "epub"]),
        [FileKind.Presentation] = (["pptx", "ppt", "odp"], ["pptx", "odp", "pdf", "jpg", "png"]),
        [FileKind.Spreadsheet] = (["xlsx", "xls", "ods", "csv", "tsv"], ["xlsx", "ods", "csv", "tsv", "pdf"]),
        [FileKind.Image] = (["jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "svg", "gif"], ["jpg", "png", "webp", "heic", "tiff", "avif", "bmp"]),
        [FileKind.Audio] = (["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"], ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"]),
        [FileKind.Video] = (["mp4", "mov", "mkv", "webm", "avi", "wmv", "gif"], ["mp4", "mov", "mkv", "webm", "avi", "wmv", "gif"]),
        [FileKind.Pdf] = (["pdf"], ["docx", "txt", "md", "jpg", "png"]),
        [FileKind.Archive] = (["zip", "tar", "tar.gz", "gz", "rar"], ["zip", "tar", "tar.gz"]),
    };

    static readonly Dictionary<string, string> Aliases = new()
    {
        ["jpeg"] = "jpg", ["heif"] = "heic", ["tif"] = "tiff", ["aif"] = "aiff", ["m4v"] = "mp4", ["tgz"] = "tar.gz",
    };

    public static readonly Operation StripMetadata = new("metadata:strip", "Rimuovi metadati", null, null, "senza metadati");

    /// <summary>Estensioni (senza punto) che Clementine sa leggere, alias compresi; "tar.gz" è coperto da "gz".</summary>
    public static IEnumerable<string> InputExtensions =>
        Map.Values.SelectMany(v => v.In).Concat(Aliases.Keys).Where(e => !e.Contains('.')).Distinct();

    public static string Extension(string path)
    {
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return "tar.gz";
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return Aliases.GetValueOrDefault(ext, ext);
    }

    public static IEnumerable<FileKind> KindsOf(string path)
    {
        var ext = Extension(path);
        return Map.Where(m => m.Value.In.Contains(ext)).Select(m => m.Key);
    }

    /// <summary>Conversioni per un file: tutte le uscite delle famiglie che lo leggono, tranne il suo stesso formato.</summary>
    public static IEnumerable<Operation> ConversionsFor(string path)
    {
        var ext = Extension(path);
        return Map.Where(m => m.Value.In.Contains(ext))
            .SelectMany(m => m.Value.Out.Where(o => o != ext)
                .Select(o => new Operation($"convert:{o}", o.ToUpperInvariant(), m.Key, o)));
    }

    /// <summary>Più file: solo le conversioni comuni a tutti.</summary>
    public static IReadOnlyList<Operation> ConversionsFor(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? [] : paths.Skip(1).Aggregate(ConversionsFor(paths[0]), (ops, p) => ops.Intersect(ConversionsFor(p))).ToList();
}
