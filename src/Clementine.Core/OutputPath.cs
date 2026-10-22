namespace Clementine.Core;

public static class OutputPath
{
    /// <summary>foto.heic → foto.jpg accanto all'originale; se esiste già → "foto (2).jpg", "foto (3).jpg"…</summary>
    public static string Next(string input, string extension)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var path = Path.Combine(dir, $"{name}.{extension}");
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"{name} ({n}).{extension}");
        return path;
    }
}
