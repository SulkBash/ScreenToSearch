using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

namespace screen_to_search;

internal static class StageZeroSelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var results = new Dictionary<string, object?>();
        try
        {
            using (var rawKeyboard = new RawKeyboardListener((_, _) => { }))
                results["rawKeyboardRegistered"] = true;
            var fallbackHook = new KeyboardHook(new ShortcutBinding(0x14, ShortcutModifiers.None));
            var keyPresses = 0;
            var keyReleases = 0;
            fallbackHook.Pressed += () => keyPresses++;
            fallbackHook.Released += () => keyReleases++;
            fallbackHook.OnRawKey(0x14, true);
            fallbackHook.OnRawKey(0x14, true); // A held key must not start twice.
            if (!fallbackHook.IsHeld || keyPresses != 1)
                throw new InvalidOperationException("Raw keyboard press was not tracked.");
            fallbackHook.MarkLongHold();
            fallbackHook.OnRawKey(0x14, false);
            if (fallbackHook.IsHeld || keyReleases != 1)
                throw new InvalidOperationException("Raw keyboard release was not tracked.");
            results["rawKeyboardHold"] = true;

            var capture = new MonitorCaptureService();
            var monitors = capture.GetMonitors();
            results["monitors"] = monitors.Select(m => new
            {
                m.DeviceName, m.PixelBounds, m.DpiX, m.DpiY, m.RotationDegrees,
                mappingRoundTrip = m.LocalDipToPixel(m.PixelToLocalDip(m.PixelBounds.Location)) == m.PixelBounds.Location
            }).ToArray();
            var simulated = new[]
            {
                new MonitorInfo("100% landscape", new Rectangle(0, 0, 1920, 1080), true, 96, 96, 0),
                new MonitorInfo("125% left", new Rectangle(-1920, 0, 1920, 1080), false, 120, 120, 0),
                new MonitorInfo("150% portrait", new Rectangle(1920, -200, 1440, 2560), false, 144, 144, 90),
                new MonitorInfo("150% inverted", new Rectangle(1920, 1080, 2560, 1440), false, 144, 144, 180),
                new MonitorInfo("125% portrait flipped", new Rectangle(-1440, -2560, 1440, 2560), false, 120, 120, 270)
            };
            results["coordinateMapping"] = simulated.Select(m =>
            {
                var sample = new Point(m.PixelBounds.X + m.PixelBounds.Width / 3,
                    m.PixelBounds.Y + m.PixelBounds.Height / 3);
                var roundTrip = m.LocalDipToPixel(m.PixelToLocalDip(sample));
                if (roundTrip != sample) throw new InvalidOperationException($"Mapping failed for {m.DeviceName}.");
                return new { m.DeviceName, m.DpiX, m.RotationDegrees, roundTrip = true };
            }).ToArray();
            var active = monitors.First(m => m.IsPrimary);
            var frame = await Task.Run(() => capture.Capture(active));
            results["capture"] = new { bytes = frame.Png.Length, ms = frame.CaptureTime.TotalMilliseconds,
                width = active.PixelBounds.Width, height = active.PixelBounds.Height };
            var monitorOcrTimer = System.Diagnostics.Stopwatch.StartNew();
            var monitorOcr = await new ScreenOcrPipeline().RecognizeAsync(frame.Png);
            results["monitorOcr"] = new { ms = monitorOcrTimer.ElapsedMilliseconds,
                words = monitorOcr.Lines.Sum(l => l.Words.Count) };

            using var image = new Bitmap(660, 120);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                using var font = new Font("Arial", 40, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString("Hello Screen Search", font, Brushes.Black, 15, 25);
            }
            using var jpg = new MemoryStream();
            image.Save(jpg, ImageFormat.Jpeg);
            var ocr = await new ScreenOcrPipeline().RecognizeAsync(jpg.ToArray());
            if (!ocr.Text.Contains("Hello Screen Search", StringComparison.OrdinalIgnoreCase)
                || ocr.Lines.SelectMany(l => l.Words).Any(w => w.Bounds.Width <= 0))
                throw new InvalidOperationException("In-process JPEG OCR failed.");
            results["jpegOcr"] = new { ocr.Text, words = ocr.Lines.Sum(l => l.Words.Count) };

            results["overlayExclusion"] = await OverlayExclusionProbe.RunAsync(capture);
            results["passed"] = ((string)results["overlayExclusion"]!).Contains("PASSED");
        }
        catch (Exception ex)
        {
            results["passed"] = false;
            results["error"] = ex.ToString();
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }));
        return results.TryGetValue("passed", out var passed) && passed is true ? 0 : 1;
    }
}
