using Clementine.Core;

namespace Clementine.Engines;

/// <summary>
/// Documenti, presentazioni e fogli con LibreOffice in modalità headless (opzionale: si usa solo se trovato).
/// Anche PDF → docx/pptx/epub, con i filtri di importazione PDF di LibreOffice.
/// </summary>
public sealed class LibreOfficeEngine(string? soffice) : IBatchEngine
{
    static readonly string[] FromPdf = ["docx", "pptx", "epub"];

    /// <summary>soffice.com nelle posizioni note (installazione classica o estrazione in Documenti), oppure null.</summary>
    public static string? Find() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LibreOffice"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LibreOffice"),
    }.Select(dir => Path.Combine(dir, "program", "soffice.com")).FirstOrDefault(File.Exists);

    public static bool HandlesPdfTo(string? target) => FromPdf.Contains(target);

    public bool CanHandle(Operation op) => soffice is not null && op.Id.StartsWith("convert:") &&
        (op.Kind is FileKind.Document or FileKind.Presentation or FileKind.Spreadsheet || op.Kind == FileKind.Pdf && HandlesPdfTo(op.TargetExtension));

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        await RunAsync([job], progress, ct);
        if (!File.Exists(job.Output) && !Directory.Exists(job.Output))
            throw new InvalidOperationException($"LibreOffice non sa convertire {Path.GetFileName(job.Input)} in {job.Op.TargetExtension!.ToUpperInvariant()}");
    }

    /// <summary>Più file con un solo avvio di LibreOffice (che è la parte lenta: ~4 s).</summary>
    public async Task RunAsync(IReadOnlyList<Job> jobs, IProgress<double> progress, CancellationToken ct)
    {
        // LibreOffice chiama l'uscita come l'ingresso: "relazione.docx" e "relazione.odt" nello stesso comando
        // diventerebbero entrambi "relazione.pdf". I doppioni di nome (e i filtri d'ingresso diversi) vanno in giri separati.
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rounds = jobs.GroupBy(job =>
        {
            var name = Path.GetFileNameWithoutExtension(job.Input);
            seen[name] = seen.GetValueOrDefault(name) + 1;
            return (InputFilter(job), seen[name]);
        }).Select(g => g.ToList()).ToList();

        var done = 0;
        foreach (var round in rounds)
            done = await ConvertAsync(round, done, jobs.Count, progress, ct);
    }

    async Task<int> ConvertAsync(List<Job> jobs, int done, int total, IProgress<double> progress, CancellationToken ct)
    {
        var op = jobs[0].Op;
        var target = op.TargetExtension!;
        var slides = op.Kind == FileKind.Presentation && target is "jpg" or "png";
        // LibreOffice decide da sé il nome del file prodotto: lo si fa scrivere in una cartella a parte
        var work = Directory.CreateDirectory(jobs[0].Output + ".lo").FullName;
        try
        {
            var args = new List<string> { "--headless", "--norestore", Profile, "--convert-to", slides ? "pdf" : Filter(target, op.Kind), "--outdir", work };
            if (InputFilter(jobs[0]) is { } filter) args.Add($"--infilter={filter}");
            args.AddRange(jobs.Select(j => j.Input));

            // Con più file, uno rotto fa uscire LibreOffice con errore anche se gli altri sono pronti: si salvano comunque
            Exception? failure = null;
            try { await ProcessRunner.RunAsync(soffice!, args, null, ct); }
            catch (InvalidOperationException e) { failure = e; }

            var extension = slides ? "pdf" : target == "tsv" ? "csv" : target; // il filtro TSV scrive comunque .csv
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                var produced = Path.Combine(work, $"{Path.GetFileNameWithoutExtension(job.Input)}.{extension}");
                if (!File.Exists(produced)) continue; // chi chiama lo segnala come non convertito
                if (slides) // una slide per immagine: si passa dal PDF e lo si rende come le pagine di un PDF
                    await new PdfiumEngine().RunAsync(job with { Input = produced, Op = new($"convert:{target}", target, FileKind.Pdf, target) },
                        new Progress<double>(), ct);
                else File.Move(produced, job.Output);
                progress.Report(++done / (double)total);
            }
            if (failure is not null && jobs.All(j => !File.Exists(j.Output) && !Directory.Exists(j.Output))) throw failure;
        }
        finally { Directory.Delete(work, recursive: true); }
        return done;
    }

    static string? InputFilter(Job job) =>
        Formats.Extension(job.Input) == "tsv" ? "Text - txt - csv (StarCalc):9,34,76,1"
        : job.Op.Kind == FileKind.Pdf ? (job.Op.TargetExtension == "pptx" ? "impress_pdf_import" : "writer_pdf_import")
        : null;

    static string Filter(string target, FileKind? kind) => target switch
    {
        // ogni foglio su una sola pagina, larga quanto serve: un prospetto di 19 pagine spezzate diventa 3 pagine leggibili
        "pdf" when kind == FileKind.Spreadsheet => """pdf:calc_pdf_Export:{"SinglePageSheets":{"type":"boolean","value":"true"}}""",
        "txt" => "txt:Text (encoded):UTF8",
        // separatore, virgolette, UTF-8, prima riga, -, lingua, "tutto il testo tra virgolette" = no, numeri speciali, celle come visualizzate
        "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1,,0,false,true,true",
        "tsv" => "csv:Text - txt - csv (StarCalc):9,34,76,1,,0,false,true,true",
        _ => target,
    };

    // un profilo tutto nostro: niente conflitti con un LibreOffice aperto dall'utente
    static string Profile => "-env:UserInstallation=" + new Uri(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clementine", "libreoffice-profile")).AbsoluteUri;
}
