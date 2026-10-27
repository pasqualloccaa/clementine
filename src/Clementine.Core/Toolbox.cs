using static Clementine.Core.Lang;

namespace Clementine.Core;

/// <summary>Anelli Strumenti (Ctrl) e Azioni (Alt), più l'insieme dei tre anelli per la ruota.</summary>
public static class Toolbox
{
    // shortcut: niente collage, split/join video, bip sull'audio e comprimi PDF (senza Ghostscript si perderebbe il testo selezionabile):
    // scartati dall'utente il 26/10/2026
    // proprietà e non campi: etichette e suffissi seguono la lingua anche se la si cambia con l'app aperta
    public static Operation Zip => new("tool:zip", "ZIP", null, "zip", Combines: true);
    public static Operation ImagesToPdf => new("tool:images-to-pdf", "PDF", FileKind.Image, "pdf", Combines: true);
    public static Operation MergePdf => new("tool:merge-pdf", T("Unisci", "Merge"), FileKind.Pdf, "pdf", T("unito", "merged"), Combines: true);

    static IEnumerable<Operation> ToolsFor(string path)
    {
        if (Directory.Exists(path)) return [Zip];
        var ext = Formats.Extension(path);
        var tools = new List<Operation>();
        foreach (var kind in Formats.KindsOf(path))
        {
            switch (kind)
            {
                case FileKind.Image when ext is not ("gif" or "svg"): // GIF animate e vettoriali: si rovinerebbero
                    if (ext is "jpg" or "png" or "webp" or "avif")
                        tools.Add(new("tool:compress", T("Comprimi", "Compress"), kind, null, T("compresso", "compressed")));
                    tools.Add(new("tool:resize50", T("Riduci 50%", "Shrink 50%"), kind, null, "50%"));
                    tools.Add(new("tool:resize", T("Dimensioni…", "Resize…"), kind, null, T("ridimensionata", "resized")));
                    tools.Add(new("tool:rotate90", T("Ruota 90°", "Rotate 90°"), kind, null, T("ruotata", "rotated")));
                    tools.Add(new("tool:web", T("Per il web", "For the web"), kind, "webp", "web"));
                    tools.Add(new("tool:crop", T("Ritaglia…", "Crop…"), kind, null, T("ritagliata", "cropped")));
                    tools.Add(new("tool:annotate", T("Annota…", "Annotate…"), kind, null, T("annotata", "annotated")));
                    if (ext is "jpg" or "webp" or "avif")
                        tools.Add(new("tool:target-size", T("Comprimi a…", "Compress to…"), kind, null, T("compresso", "compressed")));
                    tools.Add(ImagesToPdf);
                    tools.Add(Formats.ReadQr);
                    tools.Add(Formats.EditMetadata);
                    break;
                case FileKind.Image:
                    tools.Add(ImagesToPdf);
                    tools.Add(Formats.ReadQr);
                    break;
                case FileKind.Video:
                    tools.Add(new("tool:compress-video", T("Comprimi", "Compress"), kind, "mp4", T("compresso", "compressed")));
                    tools.Add(new("tool:web", T("Per il web", "For the web"), kind, "mp4", "web"));
                    tools.Add(new("tool:trim", T("Taglia…", "Trim…"), kind, null, T("tagliato", "trimmed")));
                    tools.Add(new("tool:speed", T("Velocità…", "Speed…"), kind, "mp4", T("velocità", "speed")));
                    tools.Add(new("tool:target-size", T("Comprimi a…", "Compress to…"), kind, "mp4", T("compresso", "compressed")));
                    tools.Add(new("tool:extract-audio", T("Estrai audio", "Extract audio"), kind, "m4a"));
                    tools.Add(new("tool:frames", T("Fotogrammi", "Frames"), kind, "jpg", T("fotogrammi", "frames")));
                    break;
                case FileKind.Audio:
                    tools.Add(new("tool:normalize", T("Normalizza", "Normalize"), kind, null, T("normalizzato", "normalized")));
                    tools.Add(new("tool:trim", T("Taglia…", "Trim…"), kind, null, T("tagliato", "trimmed")));
                    tools.Add(new("tool:mono", "Mono", kind, null, "mono"));
                    break;
                case FileKind.Archive:
                    tools.Add(new("tool:extract", T("Estrai", "Extract"), kind, null, "")); // una cartella con il nome dell'archivio, lì accanto
                    break;
                case FileKind.Pdf:
                    tools.Add(MergePdf);
                    tools.Add(new("tool:split-pdf", T("Dividi", "Split"), kind, "pdf", T("pagine", "pages")));
                    tools.Add(new("tool:pages", T("Pagine…", "Pages…"), kind, "pdf", T("pagine scelte", "selected pages")));
                    tools.Add(new("convert:txt", T("Estrai testo", "Extract text"), kind, "txt"));
                    break;
            }
        }
        // ExifTool scrive i metadati di immagini, audio, video e PDF; i documenti Office li sa solo leggere
        if (Formats.KindsOf(path).Any(k => k is FileKind.Image or FileKind.Audio or FileKind.Video or FileKind.Pdf))
            tools.Add(Formats.StripMetadata);
        tools.Add(Zip);
        return tools.Distinct();
    }

    /// <summary>Strumenti comuni a tutti i file; "Unisci" solo con almeno due file.</summary>
    public static IReadOnlyList<Operation> ToolsFor(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? [] : paths.Skip(1).Aggregate(ToolsFor(paths[0]), (ops, p) => ops.Intersect(ToolsFor(p)))
            .Where(op => op != MergePdf || paths.Count > 1).ToList();

    public static IReadOnlyList<Operation> Actions =>
    [
        new("action:copy-to", T("Copia in…", "Copy to…"), null, null),
        new("action:move-to", T("Sposta in…", "Move to…"), null, null),
        new("action:copy-path", T("Copia percorso", "Copy path"), null, null),
        new("action:terminal", T("Terminale qui", "Terminal here"), null, null),
        new("action:share", T("Condividi", "Share"), null, null),
        new("action:new-folder", T("Nuova cartella", "New folder"), null, null),
    ];

    public static bool IsAction(Operation op) => op.Id.StartsWith("action:");

    /// <summary>Strumenti che prima di partire chiedono un valore (Operation.Argument).</summary>
    public static bool AsksArgument(Operation op) => op.Id is "tool:trim" or "tool:speed" or "tool:target-size" or "tool:crop"
        or "tool:resize" or "tool:annotate" or "tool:pages" or "metadata:edit";

    /// <summary>I tre anelli, indicizzati per Ring.</summary>
    public static IReadOnlyList<Operation>[] RingsFor(IReadOnlyList<string> paths) =>
        [Formats.ConversionsFor(paths), ToolsFor(paths), paths.Count > 0 ? Actions : []];

    /// <summary>
    /// L'operazione con quell'id, solo se è davvero proponibile per questi file (stesse regole della ruota).
    /// Va usata per tutto ciò che arriva da fuori (riga di comando, named pipe).
    /// </summary>
    public static Operation? Resolve(string id, IReadOnlyList<string> paths) =>
        paths.Count == 0 ? null : RingsFor(paths).SelectMany(r => r).FirstOrDefault(o => o.Id == id);

    /// <summary>Ritrova un'operazione per id (per "Ripeti" dagli ultimi file).</summary>
    public static Operation? Find(string path, string id) =>
        Formats.ConversionsFor(path).Concat(ToolsFor(path)).FirstOrDefault(o => o.Id == id);
}
