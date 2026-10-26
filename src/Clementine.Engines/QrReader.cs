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
        image.Depth = 8; // un PNG o una scansione in bianco e nero si apre a 1 bit: 8 pixel per byte, illeggibili per ZXing
        var gray = image.ToByteArray(MagickFormat.Gray); // un byte per pixel, come vuole Gray8
        var reader = new BarcodeReaderGeneric { Options = { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
        return reader.DecodeMultiple(new RGBLuminanceSource(gray, (int)image.Width, (int)image.Height, RGBLuminanceSource.BitmapFormat.Gray8))
            ?.Select(r => r.Text).Distinct().ToArray() ?? [];
    }
}
