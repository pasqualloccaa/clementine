using Clementine.Core;
using Docnet.Core;
using Docnet.Core.Models;
using ImageMagick;

namespace Clementine.Engines;

/// <summary>PDF → testo o immagini a 300 dpi, con PDFium (Docnet). Più pagine → una cartella con un'immagine per pagina.</summary>
public sealed class PdfiumEngine : IEngine
{
    const double Dpi = 300;

    public bool CanHandle(Operation op) => op.Kind == FileKind.Pdf;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
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
            return Task.CompletedTask;
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
            var number = (i + 1).ToString().PadLeft(pages.ToString().Length, '0');
            image.Write(folder is null ? job.Output : Path.Combine(folder, $"{name} - {number}.{target}"));
            progress.Report((i + 1.0) / pages);
        }
        return Task.CompletedTask;
    }
}
