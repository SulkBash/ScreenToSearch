using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace screen_to_search;

internal static class ScreenOcr
{
    private static readonly ScreenOcrPipeline Ocr = new();

    public static Task<OcrDocument> RecognizeAsync(Rectangle area)
    {
        using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Ocr.RecognizeAsync(stream.ToArray());
    }
}
