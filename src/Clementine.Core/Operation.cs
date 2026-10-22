namespace Clementine.Core;

public enum FileKind { Image, Audio, Video, Pdf, Archive }

/// <summary>
/// Un'operazione della ruota. Kind indica quale famiglia di motori la esegue (null = qualunque file, es. metadati).
/// TargetExtension null = stessa estensione dell'originale; Suffix finisce nel nome: "foto (senza metadati).jpg".
/// </summary>
public sealed record Operation(string Id, string Label, FileKind? Kind, string? TargetExtension, string? Suffix = null);

/// <summary>
/// Un singolo file da elaborare. Output è il percorso temporaneo dove il motore scrive un file oppure,
/// se produce più file (es. una pagina per immagine), una cartella.
/// </summary>
public sealed record Job(string Input, Operation Op, string Output);

public interface IEngine
{
    bool CanHandle(Operation op);
    Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct);
}
