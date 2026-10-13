using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace screen_to_search;

internal static class OcrLifecycleSelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var results = new Dictionary<string, object?>();
        var engineField = typeof(NeuralOcrService).GetField("_engine", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool IsLoaded() => engineField.GetValue(null) is not null;
        try
        {
            if (IsLoaded()) throw new InvalidOperationException("OCR loaded before first use.");
            results["initiallyUnloaded"] = true;
            using var sample = new Bitmap(800, 180);
            using (var graphics = Graphics.FromImage(sample))
            {
                graphics.Clear(Color.White);
                using var font = new Font("Arial", 49, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString("CIRCLE IMAGE TEXT", font, Brushes.Black, 28, 45);
            }
            using var stream = new MemoryStream();
            sample.Save(stream, ImageFormat.Png);
            var image = stream.ToArray();
            var service = new NeuralOcrService();
            async Task<double> Recognize()
            {
                var elapsed = Stopwatch.StartNew();
                var document = await service.RecognizeAsync(image);
                if (!document.Text.Contains("CIRCLE IMAGE TEXT", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"OCR failed: {document.Text}");
                return elapsed.Elapsed.TotalMilliseconds;
            }
            results["coldMilliseconds"] = await Recognize();
            if (!IsLoaded()) throw new InvalidOperationException("OCR engine was not retained for reuse.");
            results["warmMilliseconds"] = await Recognize();
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try
                {
                    await service.RecognizeAsync(image, canceled.Token);
                    throw new InvalidOperationException("Canceled OCR request was accepted.");
                }
                catch (OperationCanceledException) { results["cancellation"] = true; }
            }
            // Use the real production timeout and callback, without forcing collection.
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            results["loadedPrivateMB"] = process.PrivateMemorySize64 / 1048576d;
            await Task.Delay(TimeSpan.FromSeconds(62));
            if (IsLoaded()) throw new InvalidOperationException("OCR engine was not released after inactivity.");
            results["releasedAfterIdle"] = true;
            process.Refresh();
            results["idlePrivateMB"] = process.PrivateMemorySize64 / 1048576d;
            results["reloadMilliseconds"] = await Recognize();
            results["passed"] = true;
        }
        catch (Exception ex)
        {
            results["passed"] = false;
            results["error"] = ex.ToString();
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }));
        return results["passed"] is true ? 0 : 1;
    }
}
