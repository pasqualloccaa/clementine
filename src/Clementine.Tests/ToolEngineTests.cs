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
}
