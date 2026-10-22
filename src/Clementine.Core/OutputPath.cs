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

    /// <summary>Il percorso stesso se libero, altrimenti "nome (2).ext", "nome (3).ext"… (cartelle: niente estensione).</summary>
    public static string Free(string path, bool isFolder)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isFolder ? "" : Path.GetExtension(path);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"{name} ({n}){ext}");
        return path;
    }
}

/// <summary>Copia e sposta file e cartelle dentro una cartella, senza mai sovrascrivere: i doppioni prendono " (2)".</summary>
public static class FileOps
{
    public static IReadOnlyList<string> CopyInto(IEnumerable<string> paths, string folder) =>
        paths.Select(p => Transfer(p, folder, move: false)).ToList();

    public static IReadOnlyList<string> MoveInto(IEnumerable<string> paths, string folder) =>
        paths.Select(p => Transfer(p, folder, move: true)).ToList();

    static string Transfer(string source, string folder, bool move)
    {
        var isFolder = Directory.Exists(source);
        var target = OutputPath.Free(Path.Combine(folder, Path.GetFileName(source.TrimEnd('\\', '/'))), isFolder);
        if (isFolder && Path.GetFullPath(target).StartsWith(Path.GetFullPath(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Non si può mettere una cartella dentro se stessa: {source}");

        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
        if (move && sameVolume)
        {
            if (isFolder) Directory.Move(source, target);
            else File.Move(source, target, overwrite: false);
            return target;
        }
        if (isFolder) CopyFolder(source, target);
        else File.Copy(source, target, overwrite: false);
        // tra dischi diversi lo spostamento è copia + eliminazione, e si elimina solo a copia riuscita
        if (move)
        {
            if (isFolder) Directory.Delete(source, recursive: true);
            else File.Delete(source);
        }
        return target;
    }

    static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyFolder(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
