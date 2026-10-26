namespace Clementine.Core;

public enum FileKind { Image, Audio, Video, Pdf, Archive, Document, Presentation, Spreadsheet }

/// <summary>Gli anelli della ruota: nessun modificatore / Shift, Ctrl, Alt.</summary>
public enum Ring { Convert, Tools, Actions }

/// <summary>
/// Un'operazione della ruota. Kind indica quale famiglia di motori la esegue (null = qualunque file, es. metadati).
/// TargetExtension null = stessa estensione dell'originale; Suffix finisce nel nome: "foto (senza metadati).jpg".
/// Combines: tutti i file del batch diventano un solo risultato (unisci PDF, ZIP…).
/// Argument: il valore chiesto all'utente dagli strumenti con parametri ("0:30-1:45", "2 MB", "16:9"…), vedi ToolArgs.
/// </summary>
public sealed record Operation(string Id, string Label, FileKind? Kind, string? TargetExtension, string? Suffix = null, bool Combines = false,
    string? Argument = null);

/// <summary>
/// Un file da elaborare. Output è il percorso temporaneo dove il motore scrive un file oppure,
/// se produce più file (es. una pagina per immagine), una cartella. Per le operazioni che uniscono,
/// Inputs contiene tutti i file e Input è il primo.
/// </summary>
public sealed record Job(string Input, Operation Op, string Output, IReadOnlyList<string>? Inputs = null)
{
    public IReadOnlyList<string> AllInputs => Inputs ?? [Input];
}

public interface IEngine
{
    bool CanHandle(Operation op);
    Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct);
}

/// <summary>
/// Motore che converte più file in un colpo solo (LibreOffice: un avvio invece di uno per file, 6 file 22 s → 4 s).
/// Un job senza file prodotto all'uscita conta come non riuscito; gli altri vengono salvati comunque.
/// </summary>
public interface IBatchEngine : IEngine
{
    Task RunAsync(IReadOnlyList<Job> jobs, IProgress<double> progress, CancellationToken ct);
}
