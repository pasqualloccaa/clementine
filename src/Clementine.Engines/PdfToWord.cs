using System.IO.Compression;
using System.Security;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Clementine.Engines;

/// <summary>
/// PDF → Word con testo che scorre: paragrafi nell'ordine di lettura, titoli riconosciuti dalla dimensione del carattere,
/// grassetto, corsivo e colore, tabelle, immagini al loro posto. Niente caselle di testo come l'importazione PDF di LibreOffice.
/// shortcut: i PDF scansionati restano immagini (niente OCR) e i punti elenco disegnati come grafica si perdono.
/// </summary>
static class PdfToWord
{
    const long EmuPerPoint = 12700;

    sealed record Run(string Text, bool Bold, bool Italic, string? Color);
    sealed record Paragraph(List<Run> Runs, double Size, int Lines);
    sealed record Picture(byte[] Data, string Extension, double Width, double Height);
    sealed record Table(List<List<List<Paragraph>>> Rows, double[] Widths);

    public static void Convert(string input, string output, IProgress<double> progress, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(input);
        var pages = new List<(Page Page, List<(double Top, object Item)> Items)>();
        var marginTexts = new Dictionary<string, int>(); // testi ripetuti in testa o a piè di pagina: intestazioni da togliere
        var imagePages = new Dictionary<string, int>(); // idem per le immagini: il logo su ogni pagina
        var sizes = new Dictionary<double, int>();
        PdfRectangle firstPage = default;

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (page.Number == 1) firstPage = page.MediaBox.Bounds;
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
            var blocks = UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words)).ToList();
            foreach (var letter in page.Letters) sizes[Math.Round(letter.PointSize)] = sizes.GetValueOrDefault(Math.Round(letter.PointSize)) + 1;
            foreach (var key in blocks.Where(b => InMargin(b, page)).Select(MarginKey).Distinct())
                marginTexts[key] = marginTexts.GetValueOrDefault(key) + 1;

            var items = new List<(double Top, object Item)>();
            var tables = FindTables(blocks);
            foreach (var block in blocks)
            {
                if (tables.Find(t => t.Blocks.Contains(block)) is { Blocks: not null } table)
                {
                    if (block == table.Blocks[0]) items.Add((block.BoundingBox.Top, table)); // la tabella prende il posto del suo primo blocco
                    continue;
                }
                items.Add((block.BoundingBox.Top, block));
            }
            // le immagini entrano prima del primo elemento che sta più in basso di loro
            foreach (var image in page.GetImages().OrderByDescending(i => i.BoundingBox.Top))
            {
                imagePages[ImageKey(image)] = imagePages.GetValueOrDefault(ImageKey(image)) + 1;
                var at = items.FindIndex(i => i.Top < image.BoundingBox.Top);
                items.Insert(at < 0 ? items.Count : at, (image.BoundingBox.Top, image));
            }
            pages.Add((page, items));
            progress.Report(0.8 * page.Number / pdf.NumberOfPages);
        }

        var body = sizes.Count == 0 ? 11 : sizes.MaxBy(s => s.Value).Key;
        var repeated = marginTexts.Concat(imagePages).Where(m => pdf.NumberOfPages >= 3 && m.Value * 2 >= pdf.NumberOfPages).Select(m => m.Key).ToHashSet();
        var content = new List<object>();
        foreach (var (page, items) in pages)
            foreach (var (_, item) in items)
                switch (item)
                {
                    case IPdfImage image when !repeated.Contains(ImageKey(image)) && ToPicture(image) is { } picture: content.Add(picture); break;
                    case TableBlocks table: content.Add(ToTable(table, body)); break;
                    case TextBlock block when !(InMargin(block, page) && (repeated.Contains(MarginKey(block)) || IsPageNumber(block))):
                        content.AddRange(ToParagraphs(block, body)); break;
                }
        Write(output, content, Headings(content, body), firstPage);
        progress.Report(1);
    }

    static bool InMargin(TextBlock block, Page page) =>
        block.BoundingBox.Bottom > page.Height * 0.93 || block.BoundingBox.Top < page.Height * 0.07;

    // "Pagina 3 di 10" e "Pagina 4 di 10" sono la stessa intestazione
    static string MarginKey(TextBlock block) => new(block.Text.Where(c => !char.IsDigit(c)).ToArray());

    static string ImageKey(IPdfImage image) => System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(image.RawBytes));

    static bool IsPageNumber(TextBlock block) => block.Text.Trim().All(c => char.IsDigit(c) || c is '-' or '/' or ' ');

    /// <summary>
    /// Un blocco può contenere più paragrafi (o voci di elenco): si va a capo dove la riga finisce prima del margine
    /// pur avendo spazio per la parola successiva, cioè dove chi ha scritto ha premuto Invio.
    /// </summary>
    static List<Paragraph> ToParagraphs(TextBlock block, double body)
    {
        var paragraphs = new List<Paragraph>();
        var runs = new List<Run>();
        var letters = 0;
        var weighted = 0.0;
        var lines = 0;
        void Flush()
        {
            if (runs.Count > 0) paragraphs.Add(new Paragraph(runs, letters == 0 ? body : weighted / letters, lines));
            runs = [];
            (letters, weighted, lines) = (0, 0, 0);
        }

        var all = block.TextLines;
        for (var l = 0; l < all.Count; l++)
        {
            var line = all[l];
            var next = l + 1 < all.Count ? all[l + 1].Words.FirstOrDefault() : null;
            var breakAfter = next is not null &&
                block.BoundingBox.Right - line.BoundingBox.Right > next.BoundingBox.Width + next.Letters[0].PointSize;
            lines++;
            for (var w = 0; w < line.Words.Count; w++)
            {
                var word = line.Words[w];
                var text = word.Text;
                var lastOfLine = w == line.Words.Count - 1;
                // a capo con trattino: "conver-" + "sione" torna "conversione"
                if (lastOfLine && !breakAfter && text.Length > 1 && text.EndsWith('-') && next?.Text is [var first, ..] && char.IsLower(first))
                    text = text[..^1];
                else if (!lastOfLine || next is not null && !breakAfter) text += " ";
                AddRun(runs, text, word);
                letters += word.Letters.Count;
                weighted += word.Letters.Sum(x => x.PointSize);
            }
            if (breakAfter) Flush();
        }
        Flush();
        return paragraphs;
    }

    static void AddRun(List<Run> runs, string text, Word word)
    {
        var letter = word.Letters[0];
        var name = word.FontName ?? "";
        var bold = letter.FontDetails?.IsBold == true || name.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        var italic = letter.FontDetails?.IsItalic == true || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        var (r, g, b) = letter.Color?.ToRGBValues() ?? (0, 0, 0);
        var color = r + g + b < 0.3 ? null : $"{(int)(r * 255):X2}{(int)(g * 255):X2}{(int)(b * 255):X2}"; // nero e quasi nero: colore normale
        var run = new Run(text, bold, italic, color);
        if (runs.Count > 0 && runs[^1] with { Text = "" } == run with { Text = "" }) runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        else runs.Add(run);
    }

    /// <summary>
    /// Livello di titolo per ogni paragrafo: le dimensioni di carattere più grandi del testo, dalla maggiore, diventano Titolo 1, 2, 3.
    /// Un titolo andato a capo su due blocchi (interlinea larga) torna un titolo solo.
    /// </summary>
    static Dictionary<Paragraph, int> Headings(List<object> content, double body)
    {
        static bool Candidate(Paragraph p, double body) => p.Lines <= 3 && p.Size >= body * 1.15;
        var levels = content.OfType<Paragraph>().Where(p => Candidate(p, body)).Select(p => Math.Round(p.Size))
            .Distinct().OrderDescending().Select((size, i) => (size, level: Math.Min(i + 1, 3))).ToDictionary(x => x.size, x => x.level);
        var headings = new Dictionary<Paragraph, int>();
        for (var i = 0; i < content.Count; i++)
        {
            if (content[i] is not Paragraph p || !Candidate(p, body)) continue;
            var level = levels[Math.Round(p.Size)];
            if (i > 0 && content[i - 1] is Paragraph previous && headings.GetValueOrDefault(previous) == level)
            {
                var merged = previous with { Runs = [.. previous.Runs.SkipLast(1), previous.Runs[^1] with { Text = previous.Runs[^1].Text + " " }, .. p.Runs] };
                headings.Remove(previous);
                content[i - 1] = merged;
                content.RemoveAt(i--);
                headings[merged] = level;
                continue;
            }
            headings[p] = level;
        }
        return headings;
    }

    // ---- tabelle ----

    sealed record TableBlocks(List<TextBlock> Blocks, List<List<List<TextBlock>>> Rows, double[] Lefts, double Right);

    /// <summary>
    /// Tabelle riconosciute dalla geometria: almeno due righe di blocchi affiancati con lo stesso bordo superiore
    /// e le colonne allineate. Un blocco in più dentro una cella (secondo paragrafo) va nella riga sopra di lui.
    /// </summary>
    static List<TableBlocks> FindTables(List<TextBlock> blocks)
    {
        var rows = new List<List<TextBlock>>();
        foreach (var block in blocks.OrderByDescending(b => b.BoundingBox.Top))
            if (rows.Count > 0 && Math.Abs(rows[^1][0].BoundingBox.Top - block.BoundingBox.Top) <= 3) rows[^1].Add(block);
            else rows.Add([block]);
        rows = rows.Select(r => r.OrderBy(b => b.BoundingBox.Left).ToList())
            .Where(r => r.Count >= 2 && r.Zip(r.Skip(1)).All(p => p.Second.BoundingBox.Left >= p.First.BoundingBox.Right - 1)).ToList();

        var tables = new List<TableBlocks>();
        for (var i = 0; i < rows.Count;)
        {
            var lefts = rows[i].Select(b => b.BoundingBox.Left).ToArray();
            var j = i + 1;
            while (j < rows.Count && rows[j].Count == lefts.Length && rows[j].Select(b => b.BoundingBox.Left).Zip(lefts).All(p => Math.Abs(p.First - p.Second) <= 8)) j++;
            if (j - i >= 2) tables.Add(Collect(blocks, rows[i..j], lefts));
            i = j;
        }
        return tables;
    }

    static TableBlocks Collect(List<TextBlock> blocks, List<List<TextBlock>> rows, double[] lefts)
    {
        var top = rows[0][0].BoundingBox.Top;
        var bottom = rows[^1].Min(b => b.BoundingBox.Bottom);
        var cells = rows.Select(r => r.Select(b => new List<TextBlock> { b }).ToList()).ToList();
        var right = rows.SelectMany(r => r).Max(b => b.BoundingBox.Right);
        foreach (var extra in blocks.Except(rows.SelectMany(r => r)))
        {
            var box = extra.BoundingBox;
            if (box.Top > top + 1 || box.Bottom < bottom - 1 || box.Left < lefts[0] - 8 || box.Left > right) continue;
            var row = rows.FindLastIndex(r => r[0].BoundingBox.Top >= box.Top - 3);
            var column = Array.FindLastIndex(lefts, x => x <= box.Left + 8);
            if (row < 0 || column < 0) continue;
            cells[row][column].Add(extra);
            right = Math.Max(right, box.Right);
        }
        // in ordine di lettura: il primo blocco della lista è quello che la tabella sostituisce
        var members = cells.SelectMany(r => r.SelectMany(c => c)).ToHashSet();
        return new TableBlocks(blocks.Where(members.Contains).ToList(), cells, lefts, right);
    }

    static Table ToTable(TableBlocks table, double body)
    {
        var edges = table.Lefts.Append(table.Right + 6).ToArray();
        var widths = edges.Zip(edges.Skip(1), (a, b) => b - a).ToArray();
        var rows = table.Rows.Select(r => r.Select(cell => cell.OrderByDescending(b => b.BoundingBox.Top)
            .SelectMany(b => ToParagraphs(b, body)).ToList()).ToList()).ToList();
        return new Table(rows, widths);
    }

    static Picture? ToPicture(IPdfImage image)
    {
        if (image.BoundingBox.Width < 8 || image.BoundingBox.Height < 8) return null; // puntini e linee decorative
        if (image.TryGetPng(out var png)) return new Picture(png, "png", image.BoundingBox.Width, image.BoundingBox.Height);
        var raw = image.RawBytes.ToArray();
        return raw is [0xFF, 0xD8, ..] ? new Picture(raw, "jpeg", image.BoundingBox.Width, image.BoundingBox.Height) : null;
    }

    // ---- DOCX scritto a mano: quattro file XML in uno ZIP ----

    static void Write(string output, List<object> content, Dictionary<Paragraph, int> headings, PdfRectangle page)
    {
        var width = page.Width > 0 ? page.Width : 595;
        var height = page.Height > 0 ? page.Height : 842;
        const double margin = 72;
        var usable = width - 2 * margin;
        static int Twips(double pt) => (int)Math.Round(pt * 20);

        var body = new StringBuilder();
        var pictures = new List<Picture>();
        void AppendParagraph(Paragraph p, int heading, bool inCell = false)
        {
            body.Append("<w:p>");
            if (heading > 0) body.Append($"<w:pPr><w:pStyle w:val=\"Heading{heading}\"/></w:pPr>");
            else if (inCell) body.Append("<w:pPr><w:spacing w:after=\"0\"/></w:pPr>");
            foreach (var run in p.Runs)
            {
                var props = (run.Bold ? "<w:b/>" : "") + (run.Italic ? "<w:i/>" : "") + (run.Color is null ? "" : $"<w:color w:val=\"{run.Color}\"/>");
                body.Append("<w:r>").Append(props.Length > 0 ? $"<w:rPr>{props}</w:rPr>" : "")
                    .Append($"<w:t xml:space=\"preserve\">{Xml(run.Text)}</w:t></w:r>");
            }
            body.Append("</w:p>");
        }

        foreach (var item in content)
            switch (item)
            {
                case Picture picture:
                    pictures.Add(picture);
                    var scale = Math.Min(1, usable / picture.Width);
                    body.Append($"<w:p><w:r>{Drawing(pictures.Count, (long)(picture.Width * scale * EmuPerPoint), (long)(picture.Height * scale * EmuPerPoint))}</w:r></w:p>");
                    break;
                case Table table:
                    var fit = Math.Min(1, usable / table.Widths.Sum());
                    var columns = table.Widths.Select(w => Twips(w * fit)).ToArray();
                    body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>")
                        .Append(string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(side => $"<w:{side} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>")))
                        .Append("</w:tblBorders><w:tblCellMar><w:top w:w=\"60\" w:type=\"dxa\"/><w:bottom w:w=\"60\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>")
                        .Append(string.Concat(columns.Select(c => $"<w:gridCol w:w=\"{c}\"/>"))).Append("</w:tblGrid>");
                    foreach (var row in table.Rows)
                    {
                        body.Append("<w:tr>");
                        foreach (var (cell, c) in row.Select((cell, c) => (cell, c)))
                        {
                            body.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{columns[c]}\" w:type=\"dxa\"/></w:tcPr>");
                            foreach (var p in cell) AppendParagraph(p, 0, inCell: true);
                            if (cell.Count == 0) body.Append("<w:p/>"); // una cella vuota deve avere comunque un paragrafo
                            body.Append("</w:tc>");
                        }
                        body.Append("</w:tr>");
                    }
                    body.Append("</w:tbl><w:p/>"); // un paragrafo dopo: due tabelle di fila non si fondono
                    break;
                case Paragraph p:
                    AppendParagraph(p, headings.GetValueOrDefault(p));
                    break;
            }
        body.Append($"<w:sectPr><w:pgSz w:w=\"{Twips(width)}\" w:h=\"{Twips(height)}\"/>" +
                    $"<w:pgMar w:top=\"{Twips(margin)}\" w:right=\"{Twips(margin)}\" w:bottom=\"{Twips(margin)}\" w:left=\"{Twips(margin)}\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>");

        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        void Entry(string name, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        Entry("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Default Extension="png" ContentType="image/png"/>
            <Default Extension="jpeg" ContentType="image/jpeg"/>
            <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
            </Types>
            """);
        Entry("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);
        Entry("word/_rels/document.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
            """<Relationship Id="rStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""" +
            string.Concat(pictures.Select((p, i) => $"""<Relationship Id="rImg{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image{i + 1}.{p.Extension}"/>""")) +
            "</Relationships>");
        Entry("word/styles.xml", Styles);
        Entry("word/document.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" """ +
            """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" """ +
            """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">""" +
            $"<w:body>{body}</w:body></w:document>");
        foreach (var (picture, i) in pictures.Select((p, i) => (p, i)))
            using (var stream = zip.CreateEntry($"word/media/image{i + 1}.{picture.Extension}").Open()) stream.Write(picture.Data);
    }

    static string Drawing(int id, long cx, long cy) =>
        $"""<w:drawing><wp:inline><wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="{id}" name="Immagine {id}"/>""" +
        $"""<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>""" +
        $"""<pic:nvPicPr><pic:cNvPr id="{id}" name="Immagine {id}"/><pic:cNvPicPr/></pic:nvPicPr>""" +
        $"""<pic:blipFill><a:blip r:embed="rImg{id}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>""" +
        $"""<pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>""" +
        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";

    static string Xml(string text) => SecurityElement.Escape(new string(text.Where(XmlAllowed).ToArray()));

    static bool XmlAllowed(char c) => c is '\t' or '\n' or '\r' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF;

    // stili con i nomi standard di Word: i titoli finiscono nel riquadro di spostamento e nell'indice
    const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:lang w:val="it-IT"/></w:rPr></w:rPrDefault>
        <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="264" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="360" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="36"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:sz w:val="28"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="60"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:sz w:val="24"/></w:rPr></w:style>
        </w:styles>
        """;
}
