namespace Clementine.Core;

/// <summary>Anelli Strumenti (Ctrl) e Azioni (Alt), più l'insieme dei tre anelli per la ruota.</summary>
public static class Toolbox
{
    // shortcut: niente strumenti che chiedono parametri o un editor (ritaglia, annota, redazione, trim, split video,
    // velocità, join video, riordina PDF, comprimi PDF, collage, leggi/modifica metadati, QR): servono interfacce dedicate
    public static readonly Operation Zip = new("tool:zip", "ZIP", null, "zip", Combines: true);
    public static readonly Operation ImagesToPdf = new("tool:images-to-pdf", "PDF", FileKind.Image, "pdf", Combines: true);
    public static readonly Operation MergePdf = new("tool:merge-pdf", "Unisci", FileKind.Pdf, "pdf", "unito", Combines: true);

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
                        tools.Add(new("tool:compress", "Comprimi", kind, null, "compresso"));
                    tools.Add(new("tool:resize50", "Riduci 50%", kind, null, "50%"));
                    tools.Add(new("tool:rotate90", "Ruota 90°", kind, null, "ruotata"));
                    tools.Add(new("tool:web", "Per il web", kind, "webp", "web"));
                    tools.Add(ImagesToPdf);
                    break;
                case FileKind.Image:
                    tools.Add(ImagesToPdf);
                    break;
                case FileKind.Video:
                    tools.Add(new("tool:compress-video", "Comprimi", kind, "mp4", "compresso"));
                    tools.Add(new("tool:web", "Per il web", kind, "mp4", "web"));
                    tools.Add(new("tool:extract-audio", "Estrai audio", kind, "m4a"));
                    tools.Add(new("tool:frames", "Fotogrammi", kind, "jpg", "fotogrammi"));
                    break;
                case FileKind.Audio:
                    tools.Add(new("tool:normalize", "Normalizza", kind, null, "normalizzato"));
                    tools.Add(new("tool:mono", "Mono", kind, null, "mono"));
                    break;
                case FileKind.Pdf:
                    tools.Add(MergePdf);
                    tools.Add(new("tool:split-pdf", "Dividi", kind, "pdf", "pagine"));
                    tools.Add(new("convert:txt", "Estrai testo", kind, "txt"));
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

    public static readonly IReadOnlyList<Operation> Actions =
    [
        new("action:copy-to", "Copia in…", null, null),
        new("action:move-to", "Sposta in…", null, null),
        new("action:copy-path", "Copia percorso", null, null),
        new("action:terminal", "Terminale qui", null, null),
        new("action:share", "Condividi", null, null),
        new("action:new-folder", "Nuova cartella", null, null),
    ];

    public static bool IsAction(Operation op) => op.Id.StartsWith("action:");

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
