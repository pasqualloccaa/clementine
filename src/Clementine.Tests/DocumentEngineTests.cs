using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Clementine.Core;
using Clementine.Engines;
using ImageMagick;
using SharpCompress.Readers;
using Xunit;

namespace Clementine.Tests;

public class PdfiumEngineTests
{
    static readonly IProgress<double> NoProgress = new Progress<double>();
    static Operation Op(string ext) => new($"convert:{ext}", ext.ToUpperInvariant(), FileKind.Pdf, ext);

    /// <summary>PDF minimo scritto a mano: una pagina Letter per testo, con xref corretto.</summary>
    internal static void WritePdf(string path, params string[] pageTexts)
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", pageTexts.Select((_, i) => $"{4 + 2 * i} 0 R"))}] /Count {pageTexts.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (var text in pageTexts)
        {
            var content = $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET";
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {objs.Count + 2} 0 R >>");
            objs.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("md")]
    public async Task ExtractsTextOfEveryPage(string ext)
    {
        using var dir = new TempDir();
        var pdf = Path.Combine(dir.Path, "doc.pdf");
        WritePdf(pdf, "Ciao Clementine", "Seconda pagina");
        var output = Path.Combine(dir.Path, $"doc.{ext}");

        await new PdfiumEngine().RunAsync(new Job(pdf, Op(ext), output), NoProgress, default);

        var text = File.ReadAllText(output);
        Assert.Contains("Ciao Clementine", text);
        Assert.Contains("Seconda pagina", text);
    }

    [Fact]
    public async Task OnePage_WritesOneImage_At300Dpi_OnWhite()
    {
        using var dir = new TempDir();
        var pdf = Path.Combine(dir.Path, "doc.pdf");
        WritePdf(pdf, "Una pagina");
        var output = Path.Combine(dir.Path, "doc.jpg");

        await new PdfiumEngine().RunAsync(new Job(pdf, Op("jpg"), output), NoProgress, default);

        using var image = new MagickImage(output);
        Assert.Equal((2550u, 3300u), (image.Width, image.Height)); // 8.5 x 11 pollici a 300 dpi
        Assert.True(image.GetPixels().GetPixel(5, 5).ToColor()!.R > 250);
    }

    [Fact]
    public async Task ManyPages_WritesFolder_OneImagePerPage()
    {
        using var dir = new TempDir();
        var pdf = Path.Combine(dir.Path, "doc.pdf");
        WritePdf(pdf, "Uno", "Due", "Tre");
        var output = Path.Combine(dir.Path, "tmp.png");

        await new PdfiumEngine().RunAsync(new Job(pdf, Op("png"), output), NoProgress, default);

        Assert.Equal(["doc - 1.png", "doc - 2.png", "doc - 3.png"],
            Directory.GetFiles(output).Select(Path.GetFileName).Order().ToArray());
    }
}

public class ArchiveEngineTests
{
    static Operation Op(string ext) => new($"convert:{ext}", ext.ToUpperInvariant(), FileKind.Archive, ext);

    [Theory]
    [InlineData("tar")]
    [InlineData("tar.gz")]
    [InlineData("zip")]
    public async Task Repacks_KeepingPathsAndContent(string target)
    {
        using var dir = new TempDir();
        var zip = Path.Combine(dir.Path, "a.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("a.txt").Open())) w.Write("primo");
            using (var w = new StreamWriter(archive.CreateEntry("sub/b.txt").Open())) w.Write("secondo");
        }
        var output = Path.Combine(dir.Path, $"out.{target}");
        var reports = new List<double>();

        await new ArchiveEngine().RunAsync(new Job(zip, Op(target), output), new SyncProgress(reports.Add), default);

        var entries = new Dictionary<string, string>();
        using (var reader = ReaderFactory.OpenReader(File.OpenRead(output), new ReaderOptions()))
            while (reader.MoveToNextEntry())
                if (!reader.Entry.IsDirectory)
                    entries[reader.Entry.Key!] = new StreamReader(reader.OpenEntryStream()).ReadToEnd();
        Assert.Equal("primo", entries["a.txt"]);
        Assert.Equal("secondo", entries["sub/b.txt"]);
        Assert.All(reports, p => Assert.InRange(p, 0, 1));
    }

    static readonly Operation Extract = new("tool:extract", "Estrai", FileKind.Archive, null, "");

    [Fact]
    public async Task Extract_KeepsFolders()
    {
        using var dir = new TempDir();
        var zip = Path.Combine(dir.Path, "Foto.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("a.txt").Open())) w.Write("primo");
            using (var w = new StreamWriter(archive.CreateEntry("sub/b.txt").Open())) w.Write("secondo");
        }
        var output = Path.Combine(dir.Path, "out");

        await new ArchiveEngine().RunAsync(new Job(zip, Extract, output), new Progress<double>(), default);

        Assert.Equal("primo", File.ReadAllText(Path.Combine(output, "a.txt")));
        Assert.Equal("secondo", File.ReadAllText(Path.Combine(output, "sub", "b.txt")));
        Assert.Equal(Path.Combine(dir.Path, "Foto"), OutputPath.Next(zip, null, Extract.Suffix)); // la cartella prende il nome dell'archivio
        Assert.Equal(Path.Combine(dir.Path, "Foto"), OutputPath.Next(Path.Combine(dir.Path, "Foto.tar.gz"), null, ""));
    }

    // "zip slip": una voce "../" non deve poter scrivere fuori dalla cartella di destinazione
    [Fact]
    public async Task Extract_RefusesEntriesOutsideTheFolder()
    {
        using var dir = new TempDir();
        var zip = Path.Combine(dir.Path, "cattivo.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("../fuori.txt").Open())) w.Write("x");
        var output = Path.Combine(dir.Path, "dentro", "out");

        await Assert.ThrowsAnyAsync<Exception>(() => new ArchiveEngine().RunAsync(new Job(zip, Extract, output), new Progress<double>(), default));

        Assert.False(File.Exists(Path.Combine(dir.Path, "dentro", "fuori.txt")));
    }
}

public class SubtitleEngineTests
{
    static Operation Op(string ext) => new($"convert:{ext}", ext.ToUpperInvariant(), FileKind.Subtitle, ext);

    static async Task<string> Convert(TempDir dir, string name, byte[] content, string target)
    {
        var input = Path.Combine(dir.Path, name);
        File.WriteAllBytes(input, content);
        var output = Path.Combine(dir.Path, $"out.{target}");
        await new SubtitleEngine().RunAsync(new Job(input, Op(target), output), new Progress<double>(), default);
        return File.ReadAllText(output, Encoding.UTF8);
    }

    [Fact]
    public async Task Srt_InWindows1252_BecomesVtt()
    {
        using var dir = new TempDir();
        var srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nPerché l’hai fatto?\r\n\r\n" +
                  "2\r\n01:02:03,040 --> 01:02:04,000\r\n<font color=\"#ffff00\"><i>Città</i></font>\r\nseconda riga\r\n\r\n";

        var vtt = await Convert(dir, "a.srt", CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(srt), "vtt");

        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nPerché l’hai fatto?\n\n" +
                     "01:02:03.040 --> 01:02:04.000\n<i>Città</i>\nseconda riga\n\n", vtt);
    }

    [Fact]
    public async Task Vtt_WithHeaderNotesAndSettings_BecomesNumberedSrt()
    {
        using var dir = new TempDir();
        var vtt = "﻿WEBVTT - prova\n\nNOTE commento\nsu due righe\n\nSTYLE\n::cue { color: yellow }\n\n" +
                  "saluto\n00:01.000 --> 00:02.000 align:start position:10%\n<v Anna>Ciao</v> <c.rosso>a tutti</c>\n\n" +
                  "1:00:00.5 --> 1:00:01.250\n<b>Fine</b>\n";

        var srt = await Convert(dir, "a.vtt", Encoding.UTF8.GetBytes(vtt), "srt");

        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nCiao a tutti\r\n\r\n" +
                     "2\r\n01:00:00,500 --> 01:00:01,250\r\n<b>Fine</b>\r\n\r\n", srt);
    }

    [Fact]
    public async Task FileWithoutCues_Fails() =>
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var dir = new TempDir();
            await Convert(dir, "vuoto.srt", Encoding.UTF8.GetBytes("non sono sottotitoli"), "vtt");
        });

    [Fact]
    public void Ring_OffersTheOtherFormat() =>
        Assert.Equal(["convert:vtt"], Formats.ConversionsFor("film.srt").Select(o => o.Id));
}

public class MetadataEngineTests
{
    internal static readonly string Exiftool = Path.Combine(AppContext.BaseDirectory, "tools", "exiftool", "exiftool.exe");

    internal static string Exif(params string[] args)
    {
        var psi = new ProcessStartInfo(Exiftool) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    internal static string JpgWithArtist(TempDir dir)
    {
        var jpg = Path.Combine(dir.Path, "foto.jpg");
        new MagickImage(MagickColors.Orange, 64, 48).Write(jpg);
        Exif("-Artist=Clementine Test", "-overwrite_original", jpg);
        Assert.Equal("Clementine Test", Exif("-s3", "-Artist", jpg));
        return jpg;
    }

    [Fact]
    public async Task Strip_RemovesMetadata_AndLeavesOriginalAlone()
    {
        Assert.True(File.Exists(Exiftool), "eseguire tools\\get-tools.ps1");
        using var dir = new TempDir();
        var jpg = JpgWithArtist(dir);
        var output = Path.Combine(dir.Path, "pulita.jpg");

        await new MetadataEngine(Exiftool).RunAsync(new Job(jpg, Formats.StripMetadata, output), new Progress<double>(), default);

        Assert.Equal("", Exif("-s3", "-Artist", output));
        Assert.Equal("Clementine Test", Exif("-s3", "-Artist", jpg));
    }

    // la modifica scrive una copia: titolo con accenti (passa dal file di argomenti UTF-8), data, GPS tolto
    [Fact]
    public async Task Edit_WritesCopy_WithAccents_AndRemovesGps()
    {
        using var dir = new TempDir();
        var jpg = JpgWithArtist(dir);
        Exif("-GPSLatitude=45.4", "-GPSLatitudeRef=N", "-GPSLongitude=9.2", "-GPSLongitudeRef=E", "-overwrite_original", jpg);
        var output = Path.Combine(dir.Path, "copia.jpg");
        var changes = new Dictionary<string, string> { ["Title"] = "Città di prova", ["Date"] = "2026:10:26 14:30:00", ["RemoveGps"] = "1" };
        var op = Formats.EditMetadata with { Argument = System.Text.Json.JsonSerializer.Serialize(changes) };

        await new MetadataEngine(Exiftool).RunAsync(new Job(jpg, op, output), new Progress<double>(), default);

        Assert.Equal("Città di prova", Exif("-s3", "-charset", "UTF8", "-XMP:Title", output));
        Assert.Equal("2026:10:26 14:30:00", Exif("-s3", "-DateTimeOriginal", output));
        Assert.Equal("", Exif("-s3", "-GPSLatitude", output));
        Assert.Equal("Clementine Test", Exif("-s3", "-Artist", output)); // i campi non toccati restano
        Assert.NotEqual("", Exif("-s3", "-GPSLatitude", jpg)); // e l'originale è com'era
    }

    [Theory]
    [InlineData("26/10/2026 14:30", "2026:10:26 14:30:00")]
    [InlineData("1/2/2025", "2025:02:01 00:00:00")]
    [InlineData("2026-10-26", null)]
    public void ExifDate(string text, string? expected) => Assert.Equal(expected, MetadataEngine.ExifDate(text));

    [Fact]
    public void QrCode_IsRead_AndPlainImageHasNone()
    {
        using var dir = new TempDir();
        var matrix = new ZXing.QrCode.QRCodeWriter().encode("https://example.com/clementine", ZXing.BarcodeFormat.QR_CODE, 300, 300);
        using (var image = new MagickImage(MagickColors.White, 300, 300))
        {
            using (var pixels = image.GetPixels()) // i pixel arrivano all'immagine quando la collezione si chiude
                for (var y = 0; y < 300; y++)
                    for (var x = 0; x < 300; x++)
                        if (matrix[x, y]) pixels.SetPixel(x, y, [0, 0, 0]);
            image.Write(Path.Combine(dir.Path, "qr.png"));
        }
        new MagickImage(MagickColors.Orange, 200, 200).Write(Path.Combine(dir.Path, "vuota.png"));

        Assert.Equal(["https://example.com/clementine"], QrReader.Read(Path.Combine(dir.Path, "qr.png")));
        // come quelli dei generatori online: moduli neri su sfondo trasparente
        using (var transparent = new MagickImage(Path.Combine(dir.Path, "qr.png")))
        {
            transparent.Transparent(MagickColors.White); // bianco → trasparente
            transparent.Write(Path.Combine(dir.Path, "trasparente.png"));
        }
        Assert.Equal(["https://example.com/clementine"], QrReader.Read(Path.Combine(dir.Path, "trasparente.png")));
        // QR enorme (moduli da ~30 px) e quasi senza margine: si legge solo rimpicciolito
        var big = new ZXing.QrCode.QRCodeWriter().encode("https://example.com/clementine", ZXing.BarcodeFormat.QR_CODE, 1000, 1000,
            new Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 1 });
        using (var huge = new MagickImage(MagickColors.Transparent, 1000, 1000))
        {
            using (var pixels = huge.GetPixels())
                for (var y = 0; y < 1000; y++)
                    for (var x = 0; x < 1000; x++)
                        if (big[x, y]) pixels.SetPixel(x, y, [0, 0, 0, 255]);
            huge.Write(Path.Combine(dir.Path, "enorme.png"));
        }
        Assert.Equal(["https://example.com/clementine"], QrReader.Read(Path.Combine(dir.Path, "enorme.png")));
        Assert.Empty(QrReader.Read(Path.Combine(dir.Path, "vuota.png")));
    }

    [Fact]
    public async Task Heic_KeepsMetadata()
    {
        var engine = new MagickEngine(Exiftool);
        var toHeic = new Operation("convert:heic", "HEIC", FileKind.Image, "heic");
        if (!engine.CanHandle(toHeic)) return; // senza estensioni HEIF/HEVC
        using var dir = new TempDir();
        var jpg = JpgWithArtist(dir);
        var heic = Path.Combine(dir.Path, "foto.heic");

        await engine.RunAsync(new Job(jpg, toHeic, heic), new Progress<double>(), default);

        Assert.Equal("Clementine Test", Exif("-s3", "-Artist", heic));
    }
}

sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
