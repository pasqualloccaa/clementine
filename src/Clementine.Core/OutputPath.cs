namespace Clementine.Core;

public static class OutputPath
{
    /// <summary>
    /// Primo nome libero accanto all'originale: foto.heic → "foto.jpg", poi "foto (2).jpg"…
    /// Con suffix: "foto (senza metadati).jpg". Con extension null: una cartella, es. "doc (PNG)".
    /// </summary>
    public static string Next(string input, string? extension, string? suffix = null)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4]; // archivio.tar.gz → archivio.zip
        if (suffix is not null) name += $" ({suffix})";
        var ext = extension is null ? "" : $".{extension}";

        var path = Path.Combine(dir, name + ext);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"{name} ({n}){ext}");
        return path;
    }
}
