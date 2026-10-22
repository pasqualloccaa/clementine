using Clementine.Core;
using Docnet.Core;
using Docnet.Core.Models;
using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Clementine.Engines;

/// <summary>
/// PDF → testo o immagini a 300 dpi, con PDFium (Docnet). Più pagine → una cartella con un'immagine per pagina.
/// Unisci e dividi con PdfPig, che copia le pagine così come sono.
/// </summary>
public sealed class PdfiumEngine : IEngine
{
    const double Dpi = 300;

    public bool CanHandle(Operation op) => op.Kind == FileKind.Pdf;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        if (job.Op.Id == Toolbox.MergePdf.Id) Merge(job, progress, ct);
        else if (job.Op.Id == "tool:split-pdf") Split(job, progress, ct);
        else Render(job, progress, ct);
        return Task.CompletedTask;
    }

static void Merge(Job job, IProgress<double> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var output = File.Create(job.Output);
        PdfMerger.Merge(output, [.. job.AllInputs]);
        progress.Report(1);
    }

    static void Split(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var folder = Directory.CreateDirectory(job.Output).FullName;
        var name = Path.GetFileNameWithoutExtension(job.Input);
        using var source = PdfDocument.Open(job.Input);
        var count = source.NumberOfPages;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var single = new PdfDocumentBuilder();
            single.AddPage(source, i + 1);
            File.WriteAllBytes(Path.Combine(folder, $"{name} - {PageNumber(i, count)}.pdf"), single.Build());
            progress.Report((i + 1.0) / count);
        }
    }

    static string PageNumber(int index, int count) => (index + 1).ToString().PadLeft(count.ToString().Length, '0');

    static void Render(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var target = job.Op.TargetExtension!;
        var text = target is "txt" or "md";
        using var doc = DocLib.Instance.GetDocReader(job.Input, new PageDimensions(text ? 1 : Dpi / 72));
        var pages = doc.GetPageCount();

        if (text)
        {
            var texts = new List<string>();
            for (var i = 0; i < pages; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var page = doc.GetPageReader(i);
                texts.Add(page.GetText().Trim());
                progress.Report((i + 1.0) / pages);
            }
            // shortcut: il .md è il testo grezzo con le pagine separate da "---", senza titoli né elenchi
            File.WriteAllText(job.Output, string.Join(target == "md" ? "\n\n---\n\n" : "\n\n", texts));
            return;
        }

        var folder = pages > 1 ? Directory.CreateDirectory(job.Output).FullName : null;
        var name = Path.GetFileNameWithoutExtension(job.Input);
        for (var i = 0; i < pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPageReader(i);
            using var image = new MagickImage();
            image.ReadPixels(page.GetImage(), new PixelReadSettings(
                (uint)page.GetPageWidth(), (uint)page.GetPageHeight(), StorageType.Char, PixelMapping.BGRA));
            image.BackgroundColor = MagickColors.White; // PDFium disegna su sfondo trasparente
            image.Alpha(AlphaAction.Remove);
            image.Density = new Density(Dpi);
            if (target == "jpg") image.Quality = 90;
            image.Write(folder is null ? job.Output : Path.Combine(folder, $"{name} - {PageNumber(i, pages)}.{target}"));
            progress.Report((i + 1.0) / pages);
        }
    }
}
