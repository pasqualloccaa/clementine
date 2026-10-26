using System.Diagnostics;
using Clementine.Core;
using Clementine.Engines;
using Docnet.Core;
using Docnet.Core.Models;
using ImageMagick;
using SharpCompress.Readers;
using Xunit;

namespace Clementine.Tests;

public class ToolEngineTests
{
    static readonly IProgress<double> NoProgress = new Progress<double>();
    static readonly string FfmpegDir = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");

    static Operation Tool(string path, string id) => Toolbox.ToolsFor([path]).Single(o => o.Id == id);

    static int PdfPages(string path)
    {
        using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(1));
        return doc.GetPageCount();
    }

    [Theory]
    [InlineData("tool:resize50", 50u, 30u)]
    [InlineData("tool:rotate90", 60u, 100u)]
    public async Task Magick_ResizeAndRotate(string id, uint width, uint height)
    {
        using var dir = new TempDir();
        var png = Path.Combine(dir.Path, "a.png");
        new MagickImage(MagickColors.Orange, 100, 60).Write(png);
        var output = Path.Combine(dir.Path, "out.png");

        await new MagickEngine().RunAsync(new Job(png, Tool(png, id), output), NoProgress, default);

        using var result = new MagickImage(output);
        Assert.Equal((width, height), (result.Width, result.Height));
    }

    [Fact]
    public async Task Magick_CompressPng_IsSmaller()
    {
        using var dir = new TempDir();
        var png = Path.Combine(dir.Path, "a.png");
        using (var noise = new MagickImage("plasma:", 400, 300)) noise.Write(png);
        var output = Path.Combine(dir.Path, "out.png");

        await new MagickEngine().RunAsync(new Job(png, Tool(png, "tool:compress"), output), NoProgress, default);

        Assert.True(new FileInfo(output).Length < new FileInfo(png).Length * 0.7);
    }

    [Fact]
    public async Task Magick_ImagesToPdf_OnePagePerImage()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "a.jpg");
        var b = Path.Combine(dir.Path, "b.png");
        new MagickImage(MagickColors.Orange, 300, 200).Write(a);
        new MagickImage(MagickColors.Green, 200, 300).Write(b);
        var output = Path.Combine(dir.Path, "out.pdf");

        await new MagickEngine().RunAsync(new Job(a, Toolbox.ImagesToPdf, output, [a, b]), NoProgress, default);

        Assert.Equal(2, PdfPages(output));
    }

    [Fact]
    public async Task Pdf_MergeAndSplit()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "a.pdf");
        var b = Path.Combine(dir.Path, "b.pdf");
        PdfiumEngineTests.WritePdf(a, "Uno", "Due");
        PdfiumEngineTests.WritePdf(b, "Tre");
        var merged = Path.Combine(dir.Path, "merged.pdf");

        await new PdfiumEngine().RunAsync(new Job(a, Toolbox.MergePdf, merged, [a, b]), NoProgress, default);
        Assert.Equal(3, PdfPages(merged));

        var split = Path.Combine(dir.Path, "split.pdf");
        await new PdfiumEngine().RunAsync(new Job(merged, Tool(merged, "tool:split-pdf"), split), NoProgress, default);
        var pages = Directory.GetFiles(split).Order().ToArray();
        Assert.Equal(["merged - 1.pdf", "merged - 2.pdf", "merged - 3.pdf"], pages.Select(Path.GetFileName));
        Assert.All(pages, p => Assert.Equal(1, PdfPages(p)));
    }

    [Fact]
    public async Task Zip_FilesAndFolders_KeepStructure()
    {
        using var dir = new TempDir();
        var file = dir.File("a.txt", "primo");
        var folder = Directory.CreateDirectory(Path.Combine(dir.Path, "cartella", "sotto")).Parent!.FullName;
        File.WriteAllText(Path.Combine(folder, "sotto", "b.txt"), "secondo");
        var output = Path.Combine(dir.Path, "out.zip");

        await new ArchiveEngine().RunAsync(new Job(file, Toolbox.Zip, output, [file, folder]), NoProgress, default);

        var entries = new Dictionary<string, string>();
        using (var reader = ReaderFactory.OpenReader(File.OpenRead(output), new ReaderOptions()))
            while (reader.MoveToNextEntry())
                entries[reader.Entry.Key!] = new StreamReader(reader.OpenEntryStream()).ReadToEnd();
        Assert.Equal("primo", entries["a.txt"]);
        Assert.Equal("secondo", entries["cartella/sotto/b.txt"]);
    }

    static string MakeMedia(TempDir dir, string name, params string[] source)
    {
        var path = Path.Combine(dir.Path, name);
        Process.Start(Path.Combine(FfmpegDir, "ffmpeg.exe"), ["-v", "error", .. source, path])!.WaitForExit();
        return path;
    }

    static string Probe(string path, string entry)
    {
        var psi = new ProcessStartInfo(Path.Combine(FfmpegDir, "ffprobe.exe"))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", entry, "-of", "csv=p=0", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }

    [Fact]
    public async Task Ffmpeg_VideoTools()
    {
        using var dir = new TempDir();
        var mov = MakeMedia(dir, "a.mov", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=10",
            "-f", "lavfi", "-i", "sine=duration=3", "-shortest");
        var engine = new FfmpegEngine(FfmpegDir);

        var compressed = Path.Combine(dir.Path, "c.mp4");
        await engine.RunAsync(new Job(mov, Tool(mov, "tool:compress-video"), compressed), NoProgress, default);
        Assert.Equal("h264", Probe(compressed, "stream=codec_name").Split('\n')[0].Trim());

        var audio = Path.Combine(dir.Path, "a.m4a");
        await engine.RunAsync(new Job(mov, Tool(mov, "tool:extract-audio"), audio), NoProgress, default);
        Assert.Equal("aac", Probe(audio, "stream=codec_name"));

        var frames = Path.Combine(dir.Path, "frames.jpg");
        await engine.RunAsync(new Job(mov, Tool(mov, "tool:frames"), frames), NoProgress, default);
        Assert.InRange(Directory.GetFiles(frames, "*.jpg").Length, 3, 4);
    }

    [Fact]
    public async Task Ffmpeg_AudioTools()
    {
        using var dir = new TempDir();
        var wav = MakeMedia(dir, "a.wav", "-f", "lavfi", "-i", "sine=duration=2", "-ac", "2");
        var engine = new FfmpegEngine(FfmpegDir);

        var mono = Path.Combine(dir.Path, "mono.wav");
        await engine.RunAsync(new Job(wav, Tool(wav, "tool:mono"), mono), NoProgress, default);
        Assert.Equal("1", Probe(mono, "stream=channels"));

        var normalized = Path.Combine(dir.Path, "norm.wav");
        await engine.RunAsync(new Job(wav, Tool(wav, "tool:normalize"), normalized), NoProgress, default);
        Assert.Equal("48000", Probe(normalized, "stream=sample_rate"));
    }

    // preset "Per il web": foto 4000 px → WebP 1920 px senza metadati; una piccola non si ingrandisce
    [Fact]
    public async Task Magick_Web_ShrinksAndStrips()
    {
        using var dir = new TempDir();
        var big = Path.Combine(dir.Path, "big.jpg");
        using (var image = new MagickImage(MagickColors.Orange, 4000, 3000))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Clementine Test");
            image.SetProfile(exif);
            image.Write(big);
        }
        var small = Path.Combine(dir.Path, "small.png");
        new MagickImage(MagickColors.Green, 300, 200).Write(small);

        var bigWeb = Path.Combine(dir.Path, "big.webp");
        var smallWeb = Path.Combine(dir.Path, "small.webp");
        await new MagickEngine().RunAsync(new Job(big, Tool(big, "tool:web"), bigWeb), NoProgress, default);
        await new MagickEngine().RunAsync(new Job(small, Tool(small, "tool:web"), smallWeb), NoProgress, default);

        using var result = new MagickImage(bigWeb);
        Assert.Equal((MagickFormat.WebP, 1920u, 1440u), (result.Format, result.Width, result.Height));
        Assert.Null(result.GetExifProfile());
        using var kept = new MagickImage(smallWeb);
        Assert.Equal((300u, 200u), (kept.Width, kept.Height));
    }

    static Operation WithArg(string path, string id, string argument) => Tool(path, id) with { Argument = argument };

    static double Duration(string path) => double.Parse(Probe(path, "format=duration"), System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Ffmpeg_ParameterTools()
    {
        using var dir = new TempDir();
        var mov = MakeMedia(dir, "a.mov", "-f", "lavfi", "-i", "testsrc=duration=6:size=1280x720:rate=30",
            "-f", "lavfi", "-i", "sine=duration=6", "-shortest", "-b:v", "8M");
        var engine = new FfmpegEngine(FfmpegDir);

        var trimmed = Path.Combine(dir.Path, "t.mov");
        await engine.RunAsync(new Job(mov, WithArg(mov, "tool:trim", "0:01-0:03"), trimmed), NoProgress, default);
        Assert.InRange(Duration(trimmed), 1.9, 2.2);

        var fast = Path.Combine(dir.Path, "f.mp4");
        await engine.RunAsync(new Job(mov, WithArg(mov, "tool:speed", "2"), fast), NoProgress, default);
        Assert.InRange(Duration(fast), 2.9, 3.2);

        var small = Path.Combine(dir.Path, "s.mp4");
        await engine.RunAsync(new Job(mov, WithArg(mov, "tool:target-size", "500 KB"), small), NoProgress, default);
        Assert.InRange(new FileInfo(small).Length, 1, 500 * 1024);

        var wav = MakeMedia(dir, "a.wav", "-f", "lavfi", "-i", "sine=duration=5");
        var cut = Path.Combine(dir.Path, "cut.wav");
        await engine.RunAsync(new Job(wav, WithArg(wav, "tool:trim", "2-5"), cut), NoProgress, default);
        Assert.InRange(Duration(cut), 2.9, 3.1);
    }

    [Fact]
    public async Task Magick_CropAndTargetSize()
    {
        using var dir = new TempDir();
        var png = Path.Combine(dir.Path, "a.png");
        new MagickImage(MagickColors.Orange, 400, 300).Write(png);
        var cropped = Path.Combine(dir.Path, "c.png");
        await new MagickEngine().RunAsync(new Job(png, WithArg(png, "tool:crop", "0.25,0.5,0.5,0.5"), cropped), NoProgress, default);
        using (var image = new MagickImage(cropped)) Assert.Equal((200u, 150u, 0, 0), (image.Width, image.Height, image.Page.X, image.Page.Y));

        var jpg = Path.Combine(dir.Path, "big.jpg");
        using (var noise = new MagickImage("plasma:", 2000, 1500)) { noise.Quality = 98; noise.Write(jpg); }
        var fitted = Path.Combine(dir.Path, "fit.jpg");
        await new MagickEngine().RunAsync(new Job(jpg, WithArg(jpg, "tool:target-size", "100 KB"), fitted), NoProgress, default);
        Assert.InRange(new FileInfo(fitted).Length, 50 * 1024, 100 * 1024); // sotto il limite, ma senza esagerare
    }

    [Theory]
    [InlineData("50", 50u, 30u)]
    [InlineData("1080", 1080u, 648u)] // anche ingrandendo
    [InlineData("80x80", 80u, 48u)]
    [InlineData("25%", 25u, 15u)]
    public async Task Magick_Resize_KeepsProportions(string value, uint width, uint height)
    {
        using var dir = new TempDir();
        var png = Path.Combine(dir.Path, "a.png");
        new MagickImage(MagickColors.Orange, 100, 60).Write(png);
        var output = Path.Combine(dir.Path, "out.png");

        await new MagickEngine().RunAsync(new Job(png, WithArg(png, "tool:resize", value), output), NoProgress, default);

        using var result = new MagickImage(output);
        Assert.Equal((width, height), (result.Width, result.Height));
    }

    [Fact]
    public async Task Magick_Annotate_DrawsOnPixels_AndDropsExifThumbnail()
    {
        using var dir = new TempDir();
        var jpg = Path.Combine(dir.Path, "a.jpg");
        using (var white = new MagickImage(MagickColors.White, 1000, 600))
        {
            white.Density = new Density(300); // le foto spesso dichiarano 300 dpi: il testo non deve diventare 4 volte più grande
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Prova");
            white.SetProfile(exif);
            white.Write(jpg);
        }
        MetadataEngineTests.Exif("-overwrite_original", $"-ThumbnailImage<={WriteThumb(dir)}", jpg);
        Assert.NotEqual("", MetadataEngineTests.Exif("-s3", "-ThumbnailImage", jpg));
        var marks = new Mark[]
        {
            new(Mark.Redact, 0.1, 0.1, 0.3, 0.3),
            new(Mark.Arrow, 0.5, 0.5, 0.9, 0.5, "#1E88E5"),
            new(Mark.Label, 0.1, 0.7, 0, 0, "#E53935", "Ciao"),
        };
        var output = Path.Combine(dir.Path, "out.png");

        await new MagickEngine().RunAsync(new Job(jpg, WithArg(jpg, "tool:annotate", ToolArgs.Marks(marks)), output), NoProgress, default);

        using var result = new MagickImage(output);
        using var pixels = result.GetPixels();
        Assert.Equal(new MagickColor("#000"), pixels.GetPixel(200, 120).ToColor()); // dentro il box nero
        var arrow = pixels.GetPixel(700, 300).ToColor()!;
        Assert.True(arrow.B > 200 && arrow.R < 100, $"freccia: {arrow}");
        // il testo sta sotto (0.1, 0.7) e alto più o meno quanto Mark.FontSize: 30 px su 600
        var red = Enumerable.Range(0, 1000).SelectMany(x => Enumerable.Range(400, 200).Select(y => (x, y)))
            .Where(p => pixels.GetPixel(p.x, p.y).ToColor() is { R: > 180, G: < 120 }).ToList();
        Assert.NotEmpty(red);
        Assert.InRange(red.Min(p => p.y), 420, 440);
        Assert.InRange(red.Max(p => p.y) - red.Min(p => p.y), 12, 45);
        var jpgOut = Path.Combine(dir.Path, "out.jpg");
        await new MagickEngine().RunAsync(new Job(jpg, WithArg(jpg, "tool:annotate", ToolArgs.Marks(marks)), jpgOut), NoProgress, default);
        Assert.Equal("", MetadataEngineTests.Exif("-s3", "-ThumbnailImage", jpgOut));
        Assert.Equal("Prova", MetadataEngineTests.Exif("-s3", "-Artist", jpgOut)); // gli altri metadati restano
    }

    static string WriteThumb(TempDir dir)
    {
        var thumb = Path.Combine(dir.Path, "thumb.jpg");
        new MagickImage(MagickColors.Green, 160, 96).Write(thumb);
        return thumb;
    }

    [Fact]
    public async Task Pdf_Pages_ReorderAndDrop()
    {
        using var dir = new TempDir();
        var pdf = Path.Combine(dir.Path, "a.pdf");
        PdfiumEngineTests.WritePdf(pdf, "Uno", "Due", "Tre");
        var output = Path.Combine(dir.Path, "out.pdf");

        await new PdfiumEngine().RunAsync(new Job(pdf, WithArg(pdf, "tool:pages", "3,1"), output), NoProgress, default);

        using var doc = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(["Tre", "Uno"], doc.GetPages().Select(p => p.Text.Trim()));
    }

    [Fact]
    public async Task Ffmpeg_WebPreset_AndSmootherGif()
    {
        using var dir = new TempDir();
        var mov = MakeMedia(dir, "a.mov", "-f", "lavfi", "-i", "testsrc=duration=2:size=1920x1080:rate=30");
        var engine = new FfmpegEngine(FfmpegDir);

        var web = Path.Combine(dir.Path, "web.mp4");
        await engine.RunAsync(new Job(mov, Tool(mov, "tool:web"), web), NoProgress, default);
        Assert.Equal("1280,720", Probe(web, "stream=width,height"));

        var gif = Path.Combine(dir.Path, "a.gif");
        await engine.RunAsync(new Job(mov, Formats.ConversionsFor(mov).Single(o => o.TargetExtension == "gif"), gif), NoProgress, default);
        Assert.Equal("640,360,25/1", Probe(gif, "stream=width,height,r_frame_rate"));
    }
}

public class ScreenRecorderTests
{
    static readonly string FfmpegDir = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");

    // registra davvero 2 secondi del primo schermo: il file resta in una cartella temporanea e si cancella
    [Fact]
    public async Task Records_AndStopsWithAPlayableMp4()
    {
        using var dir = new TempDir();
        var screens = ScreenRecorder.Screens();
        Assert.NotEmpty(screens);
        Assert.All(screens, s => Assert.True(s.Width > 0 && s.Height > 0));
        var recorder = new ScreenRecorder(FfmpegDir);
        var output = Path.Combine(dir.Path, "rec.mp4");

        recorder.Start(screens[0], output, microphone: null);
        Assert.True(recorder.IsRecording);
        await Task.Delay(2500);
        Assert.Equal(output, await recorder.StopAsync());

        Assert.False(recorder.IsRecording);
        var psi = new ProcessStartInfo(Path.Combine(FfmpegDir, "ffprobe.exe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", output])
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var duration = double.Parse(p.StandardOutput.ReadToEnd().Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 1.5, 4);
    }
}
