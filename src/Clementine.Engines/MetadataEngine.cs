using System.Globalization;
using System.Text.Json;
using Clementine.Core;

namespace Clementine.Engines;

/// <summary>Metadati con ExifTool (tools\exiftool).</summary>
public sealed class MetadataEngine(string exiftool) : IEngine
{
    public bool CanHandle(Operation op) => op.Id is "metadata:strip" or "metadata:edit" && File.Exists(exiftool);

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        // Rimuovi: tutto tranne profilo colore e orientamento, senza i quali la foto cambierebbe colori o verso.
        // Modifica: si scrive una copia ("foto (metadati).jpg"), l'originale non si tocca.
        if (job.Op.Id == Formats.StripMetadata.Id)
        {
            await ProcessRunner.RunAsync(exiftool, ["-all=", "-tagsFromFile", "@", "-ICC_Profile", "-Orientation", "-o", job.Output, job.Input], null, ct);
            progress.Report(1);
            return;
        }
        // i valori scritti dall'utente passano da un file di argomenti in UTF-8: dalla riga di comando
        // ExifTool per Windows leggerebbe "Città" con la codifica di sistema e la rovinerebbe
        var argfile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(argfile, EditArgs(JsonSerializer.Deserialize<Dictionary<string, string>>(job.Op.Argument ?? "{}") ?? []), ct);
            await ProcessRunner.RunAsync(exiftool, ["-@", argfile, "-o", job.Output, job.Input], null, ct);
        }
        finally { File.Delete(argfile); }
        progress.Report(1);
    }

    /// <summary>
    /// Le modifiche scelte nella finestra dei metadati → argomenti di ExifTool. Ogni campo va in più etichette:
    /// EXIF per le fotocamere, XMP per Lightroom e il web, XP per le proprietà di Esplora file.
    /// </summary>
    public static string[] EditArgs(IReadOnlyDictionary<string, string> changes)
    {
        var args = new List<string>();
        foreach (var (field, value) in changes)
        {
            string[] tags = field switch
            {
                "Title" => ["XMP:Title", "EXIF:ImageDescription", "XPTitle"],
                "Author" => ["EXIF:Artist", "XMP:Creator", "XPAuthor"],
                "Copyright" => ["EXIF:Copyright", "XMP:Rights"],
                "Date" => ["EXIF:DateTimeOriginal", "EXIF:CreateDate"],
                "RemoveGps" => [],
                _ => throw new ArgumentException($"Campo sconosciuto: {field}"),
            };
            args.AddRange(tags.Select(t => $"-{t}={value}"));
        }
        if (changes.ContainsKey("RemoveGps")) args.Add("-gps:all=");
        return [.. args];
    }

    /// <summary>"26/10/2026 14:30" → "2026:10:26 14:30:00" (formato EXIF); null se non è una data.</summary>
    public static string? ExifDate(string text) =>
        DateTime.TryParseExact(text.Trim(), ["d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
            : null;

    /// <summary>I metadati da mostrare nella finestra: nome dell'etichetta → valore (GPS in gradi decimali).</summary>
    public static async Task<Dictionary<string, string>> ReadAsync(string exiftool, string path, CancellationToken ct)
    {
        var json = await ProcessRunner.RunAsync(exiftool, ["-j", "-n", "-Title", "-ImageDescription", "-Artist", "-Creator", "-Copyright",
            "-DateTimeOriginal", "-Make", "-Model", "-LensModel", "-ImageSize", "-GPSLatitude", "-GPSLongitude", path], null, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[0].EnumerateObject().Where(p => p.Name != "SourceFile")
            .ToDictionary(p => p.Name, p => p.Value.ToString());
    }

    /// <summary>Copia i metadati da un file all'altro, tranne l'orientamento: i pixel sono già raddrizzati.</summary>
    public static Task CopyAsync(string exiftool, string from, string to, CancellationToken ct) =>
        ProcessRunner.RunAsync(exiftool,
            ["-tagsFromFile", from, "-all:all", "--Orientation", "-overwrite_original", to], null, ct);
}
