using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace screen_to_search;

internal static class OcrQualityProbe
{
    public static async Task<int> RunAsync(string imagePath, string reportPath)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            var image = await File.ReadAllBytesAsync(imagePath);
            var timer = Stopwatch.StartNew();
            var screen = await new ScreenOcrPipeline().RecognizeAsync(image);
            result["screen"] = new { words = screen.Lines.Sum(line => line.Words.Count),
                milliseconds = timer.ElapsedMilliseconds, text = screen.Text };
            result["textGroups"] = TextLayout.Build(OcrSelection.Flatten(screen))
                .Select(group => new { group.Text, group.Bounds }).ToArray();
            result["wordBoxes"] = OcrSelection.Flatten(screen)
                .Select(hit => new { hit.Word.Text, hit.Word.Bounds }).ToArray();
            result["passed"] = screen.Lines.Any(line => line.Words.Count > 0);
        }
        catch (Exception ex)
        {
            result["passed"] = false;
            result["error"] = ex.ToString();
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
        return result["passed"] is true ? 0 : 1;
    }
}
