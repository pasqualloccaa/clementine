using System.Runtime.InteropServices.WindowsRuntime;
using Clementine.Core;
using ImageMagick;
using Windows.Graphics.Imaging;

namespace Clementine.Engines;

/// <summary>Immagini con ImageMagick: legge tutti i formati in ingresso, HEIC compreso.</summary>
/// <param name="exiftool">Se presente, copia i metadati nei file HEIC (l'encoder di Windows non li scrive).</param>
public sealed class MagickEngine(string? exiftool = null) : IEngine
{
    // ImageMagick (come libvips) non include un encoder HEVC: per scrivere HEIC si usa quello di Windows,
    // presente solo con le estensioni "HEIF Image" e "HEVC Video" dello Store.
    static readonly Lazy<bool> HeifEncoder = new(() =>
        BitmapEncoder.GetEncoderInformationEnumerator().Any(i => i.CodecId == BitmapEncoder.HeifEncoderId));

    public bool CanHandle(Operation op) => op.Kind == FileKind.Image && (op.TargetExtension != "heic" || HeifEncoder.Value);

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        using var image = new MagickImage(job.Input); // solo il primo fotogramma/pagina
        image.AutoOrient();
        ct.ThrowIfCancellationRequested();

        var target = job.Op.TargetExtension;
        if (target is "jpg" or "bmp" or "heic")
        {
            // formati senza trasparenza: sfondo bianco invece del nero
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }
        if (target is "jpg" or "webp" or "avif") image.Quality = 90;
        if (target == "jpg")
        {
            // con qualità >= 90 ImageMagick passerebbe a 4:4:4 + Huffman ottimizzato: scrittura 2-3x più lenta
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
            image.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "false");
        }

        if (target == "heic")
        {
            await WriteHeicAsync(image, job.Output);
            if (File.Exists(exiftool)) await MetadataEngine.CopyAsync(exiftool, job.Input, job.Output, ct);
        }
        else image.Write(job.Output);
        progress.Report(1);
    }

    static async Task WriteHeicAsync(MagickImage image, string path)
    {
        var pixels = image.ToByteArray(MagickFormat.Bgra);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, (int)image.Width, (int)image.Height, BitmapAlphaMode.Ignore);
        using var file = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, file.AsRandomAccessStream());
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }
}
