using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace screen_to_search;

internal static class OcrModelComparison
{
    public static async Task<int> RunAsync(string imagePath, string reportPath)
    {
        var image = await File.ReadAllBytesAsync(imagePath);
        var service = new NeuralOcrService();
        var report = new Dictionary<string, object?>();
        foreach (var multilingual in new[] { false, true })
        {
            var timer = Stopwatch.StartNew();
            var cold = multilingual ? await service.RecognizeMultilingualAsync(image)
                : await service.RecognizeAsync(image, maxSide: 2048);
            var coldMs = timer.ElapsedMilliseconds;
            timer.Restart();
            var warm = multilingual ? await service.RecognizeMultilingualAsync(image)
                : await service.RecognizeAsync(image, maxSide: 2048);
            report[multilingual ? "multilingual" : "latin"] = new
            {
                coldMs,
                warmMs = timer.ElapsedMilliseconds,
                words = warm.Lines.Sum(line => line.Words.Count),
                text = warm.Text,
                coldText = cold.Text,
                lines = warm.Lines.Select(line => new
                {
                    line.Text,
                    words = line.Words.Select(word => new { word.Text, word.Bounds.X, word.Bounds.Width })
                })
            };
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
