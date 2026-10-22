namespace Clementine.Core;

/// <summary>Mappa formati (CLAUDE.md, parità con Tangerine) e conversioni proposte dalla ruota.</summary>
public static class Formats
{
    static readonly Dictionary<FileKind, (string[] In, string[] Out)> Map = new()
    {
        [FileKind.Image] = (["jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "svg", "gif"], ["jpg", "png", "webp", "heic", "tiff", "avif", "bmp"]),
        [FileKind.Audio] = (["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"], ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"]),
        [FileKind.Video] = (["mp4", "mov", "mkv", "webm", "avi", "wmv", "gif"], ["mp4", "mov", "mkv", "webm", "avi", "wmv", "gif"]),
    };

    static readonly Dictionary<string, string> Aliases = new()
    {
        ["jpeg"] = "jpg", ["heif"] = "heic", ["tif"] = "tiff", ["aif"] = "aiff", ["m4v"] = "mp4",
    };

    public static string Extension(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return Aliases.GetValueOrDefault(ext, ext);
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
