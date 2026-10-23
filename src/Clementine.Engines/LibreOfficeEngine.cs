using Clementine.Core;

namespace Clementine.Engines;

/// <summary>
/// Documenti, presentazioni e fogli con LibreOffice in modalità headless (opzionale: si usa solo se trovato).
/// Anche PDF → docx/pptx/epub, con i filtri di importazione PDF di LibreOffice.
/// </summary>
public sealed class LibreOfficeEngine(string? soffice) : IEngine
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
        var target = job.Op.TargetExtension!;
        // LibreOffice decide da sé il nome del file prodotto: lo si fa scrivere in una cartella a parte
        var work = Directory.CreateDirectory(job.Output + ".lo").FullName;
        try
        {
            var slides = job.Op.Kind == FileKind.Presentation && target is "jpg" or "png";
            var args = new List<string> { "--headless", "--norestore", Profile, "--convert-to", slides ? "pdf" : Filter(target, job.Op.Kind), "--outdir", work };
            if (Formats.Extension(job.Input) == "tsv") args.Add("--infilter=Text - txt - csv (StarCalc):9,34,76,1");
            if (job.Op.Kind == FileKind.Pdf) args.Add(target == "pptx" ? "--infilter=impress_pdf_import" : "--infilter=writer_pdf_import");
            args.Add(job.Input);

            // LibreOffice esce con 0 anche quando non converte: conta solo il file prodotto
            await ProcessRunner.RunAsync(soffice!, args, null, ct);
            var produced = Directory.GetFiles(work).SingleOrDefault()
                ?? throw new InvalidOperationException($"LibreOffice non sa convertire {Path.GetFileName(job.Input)} in {target.ToUpperInvariant()}");

            if (slides) // una slide per immagine: si passa dal PDF e lo si rende come le pagine di un PDF
                await new PdfiumEngine().RunAsync(job with { Input = produced, Op = new($"convert:{target}", target, FileKind.Pdf, target) }, progress, ct);
            else File.Move(produced, job.Output);
        }
        finally { Directory.Delete(work, recursive: true); }
        progress.Report(1);
    }

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
