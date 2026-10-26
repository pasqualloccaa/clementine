using ImageMagick;
using ZXing;

namespace Clementine.Engines;

/// <summary>Testo dei codici QR in un'immagine (ZXing.Net, tutto in locale).</summary>
public static class QrReader
{
    public static string[] Read(string path)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        // sfondo trasparente (i generatori di QR online lo fanno spesso): in grigio diventerebbe nero e il QR sparirebbe
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
        image.Depth = 8; // un PNG o una scansione in bianco e nero si apre a 1 bit: 8 pixel per byte, illeggibili per ZXing
        // margine bianco: molti QR scaricati ne hanno troppo poco
        image.BorderColor = MagickColors.White;
        image.Border(Math.Max(image.Width, image.Height) / 10);

        // ZXing decide bianco e nero a blocchi di ~40 px: con un QR enorme (moduli da 30 px) si perde dentro i quadrati neri,
        // con una foto piccola i moduli sono troppo fini. Si prova a grandezza naturale e poi rimpicciolita.
        foreach (var side in new uint[] { 0, 800, 400 })
        {
            using var attempt = image.Clone();
            if (side > 0)
            {
                if (Math.Max(attempt.Width, attempt.Height) <= side) continue;
                attempt.Resize(side, side);
            }
            var gray = attempt.ToByteArray(MagickFormat.Gray); // un byte per pixel, come vuole Gray8
            var reader = new BarcodeReaderGeneric { Options = { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
            var found = reader.DecodeMultiple(new RGBLuminanceSource(gray, (int)attempt.Width, (int)attempt.Height, RGBLuminanceSource.BitmapFormat.Gray8));
            if (found is { Length: > 0 }) return found.Select(r => r.Text).Distinct().ToArray();
        }
        return [];
    }
}
