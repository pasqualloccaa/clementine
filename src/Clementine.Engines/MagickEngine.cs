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

    static readonly string[] Tools = ["tool:compress", "tool:resize50", "tool:rotate90", "tool:web", "tool:crop", "tool:target-size", Toolbox.ImagesToPdf.Id];

    public bool CanHandle(Operation op) => op.Kind == FileKind.Image &&
        (op.Id.StartsWith("convert:") ? op.TargetExtension != "heic" || HeifEncoder.Value : Tools.Contains(op.Id));

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        if (job.Op.Id == Toolbox.ImagesToPdf.Id)
        {
            WritePdf(job, progress, ct);
            return;
        }

        using var image = new MagickImage(job.Input); // solo il primo fotogramma/pagina
        image.AutoOrient();
        ct.ThrowIfCancellationRequested();
        if (job.Op.Id == "tool:resize50") image.Resize(new Percentage(50));
        if (job.Op.Id == "tool:rotate90") image.Rotate(90);
        if (job.Op.Id == "tool:crop") Crop(image, job.Op.Argument ?? "");
        if (job.Op.Id == "tool:web") // preset: lato lungo al massimo 1920 px (mai ingrandita), niente metadati né GPS
        {
            image.Resize(new MagickGeometry(1920, 1920) { Greater = true });
            image.Strip();
        }

        var target = Formats.Extension(job.Output);
        if (target is "jpg" or "bmp" or "heic") Flatten(image);
        if (target is "jpg" or "webp" or "avif") image.Quality = job.Op.Id switch { "tool:compress" => 70u, "tool:web" => 80u, _ => 90u };
        if (target == "jpg")
        {
            // con qualità >= 90 ImageMagick passerebbe a 4:4:4 + Huffman ottimizzato: scrittura 2-3x più lenta
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
            image.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "false");
        }
        if (job.Op.Id == "tool:compress" && target == "png")
        {
            // come TinyPNG: palette da 256 colori con dithering, poi compressione massima
            image.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        }

        if (target == "heic")
        {
            await WriteHeicAsync(image, job.Output);
            if (File.Exists(exiftool)) await MetadataEngine.CopyAsync(exiftool, job.Input, job.Output, ct);
        }
        else if (job.Op.Id == "tool:target-size")
            await File.WriteAllBytesAsync(job.Output, FitSize(image, ToolArgs.Size(job.Op.Argument ?? "") ?? throw new ArgumentException($"Dimensione non valida: {job.Op.Argument}"), ct), ct);
        else image.Write(job.Output);
        progress.Report(1);
    }

    /// <summary>"16:9" → il rettangolo più grande con quelle proporzioni, al centro.</summary>
    static void Crop(MagickImage image, string ratio)
    {
        var parts = ratio.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], out var w) || !double.TryParse(parts[1], out var h) || w <= 0 || h <= 0)
            throw new ArgumentException($"Proporzioni non valide: {ratio}");
        var (width, height) = image.Width / (double)image.Height > w / h
            ? ((uint)Math.Round(image.Height * w / h), image.Height)
            : (image.Width, (uint)Math.Round(image.Width * h / w));
        image.Crop(new MagickGeometry(width, height), Gravity.Center);
        image.ResetPage();
    }

    /// <summary>
    /// La qualità più alta che sta sotto il peso voluto (ricerca binaria); se nemmeno al minimo ci sta, si rimpicciolisce del 20% e si riprova.
    /// </summary>
    internal static byte[] FitSize(MagickImage image, long bytes, CancellationToken ct)
    {
        while (true)
        {
            byte[]? best = null;
            for (uint low = 5, high = 95; low <= high;)
            {
                ct.ThrowIfCancellationRequested();
                var quality = (low + high) / 2;
                image.Quality = quality;
                var data = image.ToByteArray();
                if (data.Length <= bytes) { best = data; low = quality + 1; }
                else high = quality - 1;
            }
            if (best is not null) return best;
            if (image.Width < 64) throw new InvalidOperationException("Impossibile scendere sotto quel peso");
            image.Resize(new Percentage(80));
        }
    }

    /// <summary>Un PDF con un'immagine per pagina, nell'ordine dei file.</summary>
    static void WritePdf(Job job, IProgress<double> progress, CancellationToken ct)
    {
        using var pages = new MagickImageCollection();
        for (var i = 0; i < job.AllInputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = new MagickImage(job.AllInputs[i]); // solo il primo fotogramma
            page.AutoOrient();
            Flatten(page);
            page.Density = new Density(150); // dimensione della pagina = pixel / 150 dpi
            page.Settings.Compression = CompressionMethod.JPEG;
            page.Quality = 85;
            pages.Add(page);
            progress.Report((i + 1.0) / (job.AllInputs.Count + 1));
        }
        pages.Write(job.Output, MagickFormat.Pdf);
        progress.Report(1);
    }

    // formati senza trasparenza: sfondo bianco invece del nero
    static void Flatten(MagickImage image)
    {
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
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
