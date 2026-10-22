using System.Diagnostics;
using Clementine.Core;
using Clementine.Engines;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace Clementine.Tests;

public class EngineTests(ITestOutputHelper output)
{
    static readonly string FfmpegDir = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
    static readonly IProgress<double> NoProgress = new Progress<double>();

    static Operation Op(FileKind kind, string ext) => new($"convert:{ext}", ext.ToUpperInvariant(), kind, ext);

    [Fact]
    public async Task Magick_PngWithAlpha_ToJpg_HasWhiteBackground()
    {
        using var dir = new TempDir();
        var input = Path.Combine(dir.Path, "a.png");
        new MagickImage(MagickColors.Transparent, 20, 20).Write(input);
        var outPath = Path.Combine(dir.Path, "a.jpg");

        await new MagickEngine().RunAsync(new Job(input, Op(FileKind.Image, "jpg"), outPath), NoProgress, default);

        using var result = new MagickImage(outPath);
        Assert.Equal(MagickFormat.Jpeg, result.Format);
        Assert.True(result.GetPixels().GetPixel(10, 10).ToColor()!.R > 250);
    }

    // Criterio di parità: HEIC → JPG di una foto da iPhone (12 MP) in meno di 1 s.
    [Fact]
    public async Task Magick_Heic12MP_ToJpg_UnderOneSecond()
    {
        var engine = new MagickEngine();
        if (!engine.CanHandle(Op(FileKind.Image, "heic"))) return; // senza estensioni HEIF/HEVC non si può creare il campione

        using var dir = new TempDir();
        var png = Path.Combine(dir.Path, "foto.png");
        using (var noise = new MagickImage("plasma:", 4032, 3024)) noise.Write(png);
        var heic = Path.Combine(dir.Path, "foto.heic");
        await engine.RunAsync(new Job(png, Op(FileKind.Image, "heic"), heic), NoProgress, default);

        var sw = Stopwatch.StartNew();
        await engine.RunAsync(new Job(heic, Op(FileKind.Image, "jpg"), Path.Combine(dir.Path, "foto.jpg")), NoProgress, default);
        sw.Stop();
        output.WriteLine($"HEIC→JPG 12 MP: {sw.ElapsedMilliseconds} ms");

        using var result = new MagickImage(Path.Combine(dir.Path, "foto.jpg"));
        Assert.Equal((4032u, 3024u), (result.Width, result.Height));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"{sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("mp3", FileKind.Audio)]
    [InlineData("opus", FileKind.Audio)]
    [InlineData("mp4", FileKind.Video)]
    [InlineData("gif", FileKind.Video)]
    public async Task Ffmpeg_Converts_AndReportsProgress(string target, FileKind kind)
    {
        Assert.True(File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe")), "eseguire tools\\get-tools.ps1");
        using var dir = new TempDir();
        var input = Path.Combine(dir.Path, kind == FileKind.Audio ? "a.wav" : "a.mov");
        // sorgente generata: 3 s di tono (audio) o pattern di prova 101x75, dimensioni dispari apposta (video)
        string[] source = kind == FileKind.Audio
            ? ["-f", "lavfi", "-i", "sine=duration=3"]
            : ["-f", "lavfi", "-i", "testsrc=duration=3:size=101x75:rate=10"];
        Process.Start(Path.Combine(FfmpegDir, "ffmpeg.exe"), ["-v", "error", .. source, input])!.WaitForExit();

        var reports = new List<double>();
        var outPath = Path.Combine(dir.Path, $"out.{target}");
        await new FfmpegEngine(FfmpegDir).RunAsync(new Job(input, Op(kind, target), outPath), new SyncProgress(reports.Add), default);

        Assert.True(new FileInfo(outPath).Length > 0);
        Assert.NotEmpty(reports);
        Assert.All(reports, p => Assert.InRange(p, 0, 1));
    }

    [Fact]
    public async Task Ffmpeg_BadInput_ThrowsWithMessage()
    {
        using var dir = new TempDir();
        var input = dir.File("finto.mp4", "non è un video");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FfmpegEngine(FfmpegDir).RunAsync(new Job(input, Op(FileKind.Video, "webm"), Path.Combine(dir.Path, "o.webm")), NoProgress, default));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
