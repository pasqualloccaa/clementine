using Clementine.Core;
using Clementine.Engines;
using Docnet.Core;
using Docnet.Core.Models;
using Xunit;

namespace Clementine.Tests;

// Una sola classe: LibreOffice con lo stesso profilo non va lanciato in parallelo (e ogni conversione costa ~4 s).
public class LibreOfficeEngineTests
{
    static readonly string? Soffice = LibreOfficeEngine.Find();
    static readonly IProgress<double> NoProgress = new Progress<double>();

    static Operation Op(string input, string target) => Formats.ConversionsFor(input).Single(o => o.TargetExtension == target);

    static async Task<string> Convert(TempDir dir, string input, string target, string output)
    {
        var path = Path.Combine(dir.Path, output);
        await new LibreOfficeEngine(Soffice).RunAsync(new Job(input, Op(input, target), path), NoProgress, default);
        return path;
    }

    [Fact]
    public void WithoutLibreOffice_NothingIsClaimed()
    {
        var engine = new LibreOfficeEngine(null);
        Assert.False(engine.CanHandle(Op("a.docx", "pdf")));
        Assert.True(new PdfiumEngine().CanHandle(Op("a.pdf", "docx"))); // PDF → Word non dipende da LibreOffice
    }

    [Fact]
    public async Task Documents_Sheets_Pdf_AndSlides()
    {
        if (Soffice is null) return; // LibreOffice è opzionale

        using var dir = new TempDir();
        // testo → Word → PDF di una pagina
        var docx = await Convert(dir, dir.File("nota.txt", "Ciao Clementine"), "docx", "nota.docx");
        var pdf = await Convert(dir, docx, "pdf", "nota.pdf");
        using (var doc = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1)))
        {
            Assert.Equal(1, doc.GetPageCount());
            using var page = doc.GetPageReader(0);
            Assert.Contains("Ciao Clementine", page.GetText());
        }

        // TSV: le tabulazioni vanno lette come colonne (serve il filtro esplicito)
        var csv = await Convert(dir, dir.File("dati.tsv", "nome\tvalore\nmela\t3\n"), "csv", "dati.csv");
        Assert.Equal("nome,valore\nmela,3", File.ReadAllText(csv).Replace("\r\n", "\n").Trim());

        // foglio largo → PDF: una sola pagina per foglio invece delle colonne spezzate su più pagine
        var xlsx = await Convert(dir, dir.File("largo.csv", string.Join(",", Enumerable.Range(1, 60).Select(i => $"colonna {i}"))), "xlsx", "largo.xlsx");
        var sheetPdf = await Convert(dir, xlsx, "pdf", "largo.pdf");
        using (var doc = DocLib.Instance.GetDocReader(sheetPdf, new PageDimensions(1))) Assert.Equal(1, doc.GetPageCount());

        // presentazione → una slide come PNG (passando dal PDF); la presentazione la fa LibreOffice stesso dal PDF
        await ProcessRunner.RunAsync(Soffice, ["--headless", "--infilter=impress_pdf_import", "--convert-to", "pptx", "--outdir", dir.Path, pdf], null, default);
        var png = await Convert(dir, Path.Combine(dir.Path, "nota.pptx"), "png", "slide.png");
        Assert.True(new FileInfo(png).Length > 0);
    }

    // PDF vero (da LibreOffice) → Word: il titolo torna titolo, le righe di un paragrafo tornano un paragrafo solo,
    // e il .docx scritto a mano si riapre senza errori
    [Fact]
    public async Task PdfToWord_RebuildsHeadingsAndParagraphs()
    {
        if (Soffice is null) return;
        using var dir = new TempDir();
        var sentence = "Questa frase serve a riempire più righe del paragrafo di prova. ";
        var md = dir.File("relazione.md", $"# Relazione annuale\n\n{string.Concat(Enumerable.Repeat(sentence, 8))}\n\nSecondo paragrafo breve.\n\n| Mese | Totale |\n|---|---|\n| Gennaio | 120 |\n| Febbraio | 95 |\n");
        var pdf = await Convert(dir, await Convert(dir, md, "docx", "relazione.docx"), "pdf", "relazione.pdf");
        var word = Path.Combine(dir.Path, "da-pdf.docx");

        await new PdfiumEngine().RunAsync(new Job(pdf, Op(pdf, "docx"), word), NoProgress, default);

        string xml;
        using (var zip = System.IO.Compression.ZipFile.OpenRead(word))
        using (var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open())) xml = reader.ReadToEnd();
        var paragraphs = System.Xml.Linq.XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "p")
            .Select(p => (Style: p.Descendants().FirstOrDefault(e => e.Name.LocalName == "pStyle")?.Attributes().First().Value,
                          Text: string.Concat(p.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value))))
            .ToList();
        Assert.Equal(("Heading1", "Relazione annuale"), paragraphs[0]);
        Assert.Equal(string.Concat(Enumerable.Repeat(sentence, 8)).Trim(), paragraphs[1].Text);
        Assert.Equal("Secondo paragrafo breve.", paragraphs[2].Text);
        var cells = System.Xml.Linq.XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "tc")
            .Select(tc => string.Concat(tc.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value))).ToArray();
        Assert.Equal(["Mese", "Totale", "Gennaio", "120", "Febbraio", "95"], cells);

        var back = await Convert(dir, word, "pdf", "riaperto.pdf");
        using var doc = DocLib.Instance.GetDocReader(back, new PageDimensions(1));
        using var page = doc.GetPageReader(0);
        Assert.Contains("Relazione annuale", page.GetText());
    }

    [Fact]
    public async Task Batch_SameNameFiles_DoNotOverwriteEachOther()
    {
        if (Soffice is null) return;
        using var dir = new TempDir();
        var txt = dir.File("relazione.txt", "dal testo");
        var md = dir.File("relazione.md", "dal markdown");
        var other = dir.File("altro.txt", "terzo");
        var op = Op(txt, "pdf");
        Job JobFor(string input, string name) => new(input, op, Path.Combine(dir.Path, name));
        var jobs = new[] { JobFor(txt, "1.pdf"), JobFor(md, "2.pdf"), JobFor(other, "3.pdf") };

        await new LibreOfficeEngine(Soffice).RunAsync(jobs, NoProgress, default);

        foreach (var (job, expected) in jobs.Zip(new[] { "dal testo", "dal markdown", "terzo" }))
        {
            using var doc = DocLib.Instance.GetDocReader(job.Output, new PageDimensions(1));
            using var page = doc.GetPageReader(0);
            Assert.Contains(expected, page.GetText());
        }
        Assert.Equal(6, Directory.GetFileSystemEntries(dir.Path).Length); // 3 originali + 3 PDF, nessuna cartella di lavoro
    }

    // LibreOffice esce con 0 anche quando non produce nulla (qui: file sparito prima della conversione)
    [Fact]
    public async Task NothingProduced_FailsWithMessage_AndLeavesNothing()
    {
        if (Soffice is null) return;
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "sparito.docx");
        var output = Path.Combine(dir.Path, "out.pdf");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LibreOfficeEngine(Soffice).RunAsync(new Job(missing, Op(missing, "pdf"), output), NoProgress, default));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path)); // niente cartelle di lavoro rimaste
    }
}
