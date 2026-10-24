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
/// grassetto, corsivo e colore, elenchi puntati, tabelle, immagini al loro posto. Niente caselle di testo come l'importazione PDF di LibreOffice.
/// shortcut: i PDF scansionati restano immagini (niente OCR) e i disegni vettoriali si perdono.
/// </summary>
static class PdfToWord
{
    const long EmuPerPoint = 12700;

    sealed record Run(string Text, bool Bold, bool Italic, string? Color);
    sealed record Paragraph(List<Run> Runs, double Size, int Lines, string Font, int List); // List: 0 niente, 1 puntato, 2 caselle
    sealed record Picture(byte[] Data, string Extension, double Width, double Height);
    sealed record Table(List<List<List<Paragraph>>> Rows, double[] Widths);

    public static void Convert(string input, string output, IProgress<double> progress, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(input);
        var pages = new List<(Page Page, List<PdfRectangle> Bullets, List<(double Top, object Item)> Items)>();
        var marginTexts = new Dictionary<string, int>(); // testi ripetuti in testa o a piè di pagina: intestazioni da togliere
        var imagePages = new Dictionary<string, int>(); // idem per le immagini: il logo su ogni pagina
        var sizes = new Dictionary<double, int>();
        var fonts = new Dictionary<string, int>();
        PdfRectangle firstPage = default;

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (page.Number == 1) firstPage = page.MediaBox.Bounds;
            var (horizontal, vertical, bullets) = Graphics(page);
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
            // le tabelle con i bordi disegnati si leggono dalla griglia, parola per parola: il resto passa all'analisi dei blocchi
            var grids = FindGrids(horizontal, vertical).Select(g => ToTableBlocks(g, words)).Where(t => t.Words!.Count > 0).ToList();
            words = words.Except(grids.SelectMany(g => g.Words!)).ToList();
            var blocks = UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words)).ToList();
            // pallini scritti come carattere in un blocco a parte (PDF da Word): sono segni di elenco come quelli disegnati
            foreach (var marker in blocks.Where(b => IsMarker(b.Text)).ToList())
            {
                bullets.Add(marker.BoundingBox);
                blocks.Remove(marker);
            }
            foreach (var letter in page.Letters)
            {
                sizes[Math.Round(letter.PointSize)] = sizes.GetValueOrDefault(Math.Round(letter.PointSize)) + 1;
                fonts[Family(letter.FontName)] = fonts.GetValueOrDefault(Family(letter.FontName)) + 1;
            }
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
            // tabelle a griglia e immagini entrano dopo l'ultimo elemento che sta più in alto di loro
            // (non prima del primo più in basso: l'ordine di lettura può mettere in testa il piè di pagina)
            void Insert(double top, object item) => items.Insert(items.FindLastIndex(i => i.Top >= top) + 1, (top, item));
            foreach (var grid in grids) Insert(grid.Top, grid);
            foreach (var image in page.GetImages().OrderByDescending(i => i.BoundingBox.Top))
            {
                imagePages[ImageKey(image)] = imagePages.GetValueOrDefault(ImageKey(image)) + 1;
                Insert(image.BoundingBox.Top, image);
            }
            pages.Add((page, bullets, items));
            progress.Report(0.8 * page.Number / pdf.NumberOfPages);
        }

        var body = sizes.Count == 0 ? 11 : sizes.MaxBy(s => s.Value).Key;
        var bodyFont = fonts.Count == 0 ? "" : fonts.MaxBy(f => f.Value).Key;
        var repeated = marginTexts.Concat(imagePages).Where(m => pdf.NumberOfPages >= 3 && m.Value * 2 >= pdf.NumberOfPages).Select(m => m.Key).ToHashSet();
        var content = new List<object>();
        foreach (var (page, bullets, items) in pages)
        {
            TextBlock? previous = null;
            foreach (var (_, item) in items)
                switch (item)
                {
                    case IPdfImage image when !repeated.Contains(ImageKey(image)) && ToPicture(image) is { } picture: content.Add(picture); break;
                    case TableBlocks table: AddTable(content, ToTable(table, body, bullets)); break;
                    case TextBlock block when !(InMargin(block, page) && (repeated.Contains(MarginKey(block)) || IsPageNumber(block))):
                        var paragraphs = ToParagraphs(block, body, bullets);
                        // pezzi della stessa riga separati da spazi larghi ("8 ott 2026 · @Pasquale", "1." e il testo della voce): restano una riga
                        if (previous is { TextLines.Count: 1 } && content.Count > 0 && content[^1] is Paragraph last && paragraphs.Count > 0
                            && Math.Abs(previous.TextLines[0].Words[0].Letters[0].StartBaseLine.Y - block.TextLines[0].Words[0].Letters[0].StartBaseLine.Y) < 2
                            && block.BoundingBox.Left > previous.BoundingBox.Right)
                        {
                            content[^1] = last with { Runs = [.. last.Runs.SkipLast(1), last.Runs[^1] with { Text = last.Runs[^1].Text + " " }, .. paragraphs[0].Runs], Lines = paragraphs[0].Lines };
                            content.AddRange(paragraphs.Skip(1));
                        }
                        // frase spezzata dal cambio pagina: "…che li integra nella" + "chat con i dati…"
                        else if (previous is null && content.Count > 0 && content[^1] is Paragraph open && paragraphs.Count > 0 && open.List == paragraphs[0].List
                                 && open.Runs[^1].Text.TrimEnd() is [.., var end] && !".:;!?".Contains(end)
                                 && paragraphs[0].Runs[0].Text is [var start, ..] && char.IsLower(start))
                        {
                            content[^1] = open with { Runs = [.. open.Runs.SkipLast(1), open.Runs[^1] with { Text = open.Runs[^1].Text.TrimEnd() + " " }, .. paragraphs[0].Runs], Lines = open.Lines + paragraphs[0].Lines };
                            content.AddRange(paragraphs.Skip(1));
                        }
                        else content.AddRange(paragraphs);
                        previous = block;
                        break;
                }
        }
        Write(output, content, Headings(content, body, bodyFont), firstPage);
        progress.Report(1);
    }

    // "BAAAAA+AnthropicSansPrint-Semibold" → "AnthropicSansPrint": la famiglia, senza sottoinsieme né stile
    static string Family(string? font)
    {
        var name = font ?? "";
        if (name.IndexOf('+') is 6) name = name[7..];
        return name.Split('-', ',')[0];
    }

    /// <summary>Una tabella spezzata su due pagine torna una: stesse colonne, e l'intestazione ripetuta si toglie.</summary>
    static void AddTable(List<object> content, Table table)
    {
        if (content.Count > 0 && content[^1] is Table previous && previous.Widths.Length == table.Widths.Length
            && previous.Widths.Zip(table.Widths).All(w => Math.Abs(w.First - w.Second) <= 4))
        {
            var rows = table.Rows.Count > 1 && CellTexts(table.Rows[0]).SequenceEqual(CellTexts(previous.Rows[0])) ? table.Rows.Skip(1) : table.Rows;
            previous.Rows.AddRange(rows);
            return;
        }
        content.Add(table);
    }

    static IEnumerable<string> CellTexts(List<List<Paragraph>> row) => row.Select(c => string.Concat(c.SelectMany(p => p.Runs).Select(r => r.Text)).Trim());

    // fascia alta e bassa della pagina: lì stanno intestazioni e piè di pagina (anche su due righe)
    static bool InMargin(TextBlock block, Page page) =>
        block.BoundingBox.Bottom > page.Height * 0.88 || block.BoundingBox.Top < page.Height * 0.12;

    // \uF0B7 e \uF0A7: pallino e quadratino di Word nei font Symbol e Wingdings
    static bool IsMarker(string text) => text.Trim() is "•" or "◦" or "▪" or "▫" or "●" or "○" or "■" or "□" or "‣" or "∙" or "❑" or "☐" or "\uF0B7" or "\uF0A7";

    // "Pagina 3 di 10" e "Pagina 4 di 10" sono la stessa intestazione
    static string MarginKey(TextBlock block) => new(block.Text.Where(c => !char.IsDigit(c)).ToArray());

    static string ImageKey(IPdfImage image) => System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(image.RawBytes));

    static bool IsPageNumber(TextBlock block) => block.Text.Trim().All(c => char.IsDigit(c) || c is '-' or '/' or ' ');

    /// <summary>
    /// Un blocco può contenere più paragrafi (o voci di elenco): si va a capo dove la riga finisce prima del margine
    /// pur avendo spazio per la parola successiva, cioè dove chi ha scritto ha premuto Invio. Una riga con un pallino
    /// disegnato alla sua sinistra apre una voce di elenco.
    /// </summary>
    static List<Paragraph> ToParagraphs(TextBlock block, double body, List<PdfRectangle> bullets)
    {
        var paragraphs = new List<Paragraph>();
        var runs = new List<Run>();
        var letters = 0;
        var weighted = 0.0;
        var lines = 0;
        var list = 0;
        var fonts = new Dictionary<string, int>();
        void Flush()
        {
            if (runs.Count > 0)
                paragraphs.Add(new Paragraph(runs, letters == 0 ? body : weighted / letters, lines, fonts.MaxBy(f => f.Value).Key, list));
            runs = [];
            (letters, weighted, lines, list) = (0, 0, 0, 0);
            fonts.Clear();
        }

        var all = block.TextLines;
        for (var l = 0; l < all.Count; l++)
        {
            var line = all[l];
            var box = line.BoundingBox;
            if (bullets.FirstOrDefault(b => b.Right <= box.Left + 1 && box.Left - b.Right < 20 && b.Centroid.Y > box.Bottom - 2 && b.Centroid.Y < box.Top + 2) is { Width: > 0 } marker)
            {
                Flush();
                list = marker.Width > 6.5 ? 2 : 1; // i pallini sono piccoli, le caselle da spuntare più grandi
            }
            var start = 0;
            if (line.Words.Count > 1 && IsMarker(line.Words[0].Text)) // pallino come prima parola della riga
            {
                Flush();
                list = 1;
                start = 1;
            }
            var next = l + 1 < all.Count ? all[l + 1].Words.FirstOrDefault() : null;
            var breakAfter = next is not null &&
                block.BoundingBox.Right - line.BoundingBox.Right > next.BoundingBox.Width + next.Letters[0].PointSize;
            lines++;
            for (var w = start; w < line.Words.Count; w++)
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
                fonts[Family(word.FontName)] = fonts.GetValueOrDefault(Family(word.FontName)) + word.Letters.Count;
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
    /// Conta anche un carattere appena più grande ma di un'altra famiglia (titoli con grazie in un testo senza).
    /// Un titolo andato a capo su due blocchi (interlinea larga) torna un titolo solo.
    /// </summary>
    static Dictionary<Paragraph, int> Headings(List<object> content, double body, string bodyFont)
    {
        bool Candidate(Paragraph p, double body) => p.Lines <= 3 && p.List == 0 && (p.Size >= body * 1.15 || p.Size >= body * 1.05 && p.Font != bodyFont);
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

    // Blocks: i blocchi che la tabella sostituisce nell'ordine di lettura (vuoto per le griglie, che si collocano con Top e hanno le loro Words)
    sealed record TableBlocks(List<TextBlock> Blocks, List<List<List<TextBlock>>> Rows, double[] Lefts, double Right, double Top = 0, List<Word>? Words = null);

    /// <summary>Linee orizzontali e verticali (tratti o rettangoli sottili) e pallini degli elenchi disegnati sulla pagina.</summary>
    static (List<PdfRectangle> Horizontal, List<PdfRectangle> Vertical, List<PdfRectangle> Bullets) Graphics(Page page)
    {
        List<PdfRectangle> horizontal = [], vertical = [], bullets = [];
        foreach (var path in page.Paths)
            foreach (var sub in path)
            {
                if (sub.GetBoundingRectangle() is not { } box) continue;
                if (box.Height <= 2 && box.Width > 4) horizontal.Add(box);
                else if (box.Width <= 2 && box.Height > 4) vertical.Add(box);
                else if (box.Width is > 2 and < 12 && box.Height is > 2 and < 12 && Math.Abs(box.Width - box.Height) < 1.5) bullets.Add(box);
                else if (path.IsStroked) // riquadro disegnato col contorno: i suoi quattro lati
                {
                    horizontal.Add(new PdfRectangle(box.Left, box.Top, box.Right, box.Top));
                    horizontal.Add(new PdfRectangle(box.Left, box.Bottom, box.Right, box.Bottom));
                    vertical.Add(new PdfRectangle(box.Left, box.Bottom, box.Left, box.Top));
                    vertical.Add(new PdfRectangle(box.Right, box.Bottom, box.Right, box.Top));
                }
            }
        return (horizontal, vertical, bullets);
    }

    /// <summary>
    /// Griglie: linee orizzontali e verticali che si toccano. Servono almeno due celle,
    /// così un riquadro singolo (un blocco di codice incorniciato) non diventa una tabella.
    /// </summary>
    static List<(double[] Xs, double[] Ys)> FindGrids(List<PdfRectangle> horizontal, List<PdfRectangle> vertical)
    {
        static bool Touch(PdfRectangle h, PdfRectangle v) =>
            v.Left >= h.Left - 2 && v.Right <= h.Right + 2 && h.Bottom >= v.Bottom - 2 && h.Top <= v.Top + 2;
        static double[] Distinct(IEnumerable<double> values)
        {
            var result = new List<double>();
            foreach (var v in values.Order())
                if (result.Count == 0 || v - result[^1] > 4) result.Add(v);
            return [.. result];
        }

        var grids = new List<(double[], double[])>();
        var seen = new HashSet<PdfRectangle>();
        foreach (var start in horizontal)
        {
            if (!seen.Add(start)) continue;
            List<PdfRectangle> hs = [start], vs = [];
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var v in vertical.Where(v => !vs.Contains(v) && hs.Any(h => Touch(h, v))).ToList()) { vs.Add(v); changed = true; }
                foreach (var h in horizontal.Where(h => !hs.Contains(h) && vs.Any(v => Touch(h, v))).ToList()) { hs.Add(h); seen.Add(h); changed = true; }
            }
            var xs = Distinct(vs.Select(v => (v.Left + v.Right) / 2));
            var ys = Distinct(hs.Select(h => (h.Top + h.Bottom) / 2)).Reverse().ToArray(); // dall'alto
            if (xs.Length >= 2 && ys.Length >= 2 && (xs.Length - 1) * (ys.Length - 1) >= 2) grids.Add((xs, ys));
        }
        return grids;
    }

    /// <summary>Le parole dentro la griglia, cella per cella, raccolte in righe di testo come farebbe l'analisi dei blocchi.</summary>
    static TableBlocks ToTableBlocks((double[] Xs, double[] Ys) grid, List<Word> words)
    {
        var (xs, ys) = grid;
        static bool In(Word w, double left, double right, double top, double bottom) =>
            w.BoundingBox.Centroid.X > left && w.BoundingBox.Centroid.X < right && w.BoundingBox.Centroid.Y < top && w.BoundingBox.Centroid.Y > bottom;
        var inside = words.Where(w => In(w, xs[0], xs[^1], ys[0], ys[^1])).ToList();
        var text = inside.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList(); // gli spazi del PDF come parole: doppi spazi
        var rows = new List<List<List<TextBlock>>>();
        for (var r = 0; r + 1 < ys.Length; r++)
        {
            var row = new List<List<TextBlock>>();
            for (var c = 0; c + 1 < xs.Length; c++)
            {
                var cell = text.Where(w => In(w, xs[c], xs[c + 1], ys[r], ys[r + 1])).ToList();
                row.Add(cell.Count == 0 ? [] : [new TextBlock(Lines(cell))]);
            }
            if (row.Any(cell => cell.Count > 0)) rows.Add(row); // righe vuote: doppie linee, bordi spessi
        }
        return new TableBlocks([], rows, xs[..^1], xs[^1] - 6, ys[0], inside);
    }

    static List<TextLine> Lines(List<Word> words)
    {
        var lines = new List<List<Word>>();
        // per linea di base e dimensione del carattere: il riquadro delle parole in certi PDF è alto zero
        static double Baseline(Word w) => w.Letters[0].StartBaseLine.Y;
        foreach (var word in words.OrderByDescending(Baseline))
            if (lines.Count > 0 && Math.Abs(Baseline(lines[^1][0]) - Baseline(word)) < word.Letters[0].PointSize / 2) lines[^1].Add(word);
            else lines.Add([word]);
        return lines.Select(l => new TextLine(l.OrderBy(w => w.BoundingBox.Left).ToList())).ToList();
    }

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

    static Table ToTable(TableBlocks table, double body, List<PdfRectangle> bullets)
    {
        var edges = table.Lefts.Append(table.Right + 6).ToArray();
        var widths = edges.Zip(edges.Skip(1), (a, b) => b - a).ToArray();
        var rows = table.Rows.Select(r => r.Select(cell => cell.OrderByDescending(b => b.BoundingBox.Top)
            .SelectMany(b => ToParagraphs(b, body, bullets)).ToList()).ToList()).ToList();
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
            else if (p.List > 0) body.Append($"<w:pPr><w:pStyle w:val=\"ListBullet\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"{p.List}\"/></w:numPr>" +
                                           (inCell ? "<w:spacing w:after=\"0\"/>" : "") + "</w:pPr>");
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
            <Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>
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
            """<Relationship Id="rNumbering" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>""" +
            string.Concat(pictures.Select((p, i) => $"""<Relationship Id="rImg{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image{i + 1}.{p.Extension}"/>""")) +
            "</Relationships>");
        Entry("word/styles.xml", Styles);
        Entry("word/numbering.xml", Numbering);
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
        <w:style w:type="paragraph" w:styleId="ListBullet"><w:name w:val="List Bullet"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="80"/></w:pPr></w:style>
        </w:styles>
        """;

    // elenco puntato (1) ed elenco di caselle da spuntare (2)
    const string Numbering = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        <w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="singleLevel"/><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/><w:lvlJc w:val="left"/>
        <w:pPr><w:ind w:left="360" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/></w:rPr></w:lvl></w:abstractNum>
        <w:abstractNum w:abstractNumId="1"><w:multiLevelType w:val="singleLevel"/><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="☐"/><w:lvlJc w:val="left"/>
        <w:pPr><w:ind w:left="360" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Segoe UI Symbol" w:hAnsi="Segoe UI Symbol"/></w:rPr></w:lvl></w:abstractNum>
        <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
        <w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
        </w:numbering>
        """;
}
