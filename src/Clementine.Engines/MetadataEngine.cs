using Clementine.Core;

namespace Clementine.Engines;

/// <summary>Metadati con ExifTool (tools\exiftool).</summary>
public sealed class MetadataEngine(string exiftool) : IEngine
{
    public bool CanHandle(Operation op) => op.Id == Formats.StripMetadata.Id && File.Exists(exiftool);

    // Toglie tutto tranne profilo colore e orientamento: senza, la foto cambierebbe colori o verso.
    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        await ProcessRunner.RunAsync(exiftool,
            ["-all=", "-tagsFromFile", "@", "-ICC_Profile", "-Orientation", "-o", job.Output, job.Input], null, ct);
        progress.Report(1);
    }

    /// <summary>Copia i metadati da un file all'altro, tranne l'orientamento: i pixel sono già raddrizzati.</summary>
    public static Task CopyAsync(string exiftool, string from, string to, CancellationToken ct) =>
        ProcessRunner.RunAsync(exiftool,
            ["-tagsFromFile", from, "-all:all", "--Orientation", "-overwrite_original", to], null, ct);
}
