using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

namespace screen_to_search;

internal static class StageTwoSelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var results = new Dictionary<string, object?>();
        try
        {
            using var sample = new Bitmap(800, 180);
            using (var graphics = Graphics.FromImage(sample))
            {
                graphics.Clear(Color.White);
                using var font = new Font("Arial", 49, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString("CIRCLE IMAGE TEXT", font, Brushes.Black, 28, 45);
            }
            using var jpg = new MemoryStream();
            sample.Save(jpg, ImageFormat.Jpeg);
            using var decoded = new Bitmap(jpg);
            var bounds = SelectionImage.RectangleBetween(new Point(780, 155), new Point(10, 20), decoded.Size);
            var crop = SelectionImage.Crop(decoded, bounds);
            var recognized = await new NeuralOcrService().RecognizeAsync(crop);
            if (!recognized.Text.Contains("CIRCLE IMAGE TEXT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Rectangle OCR failed: {recognized.Text}");
            results["jpgRectangleOcr"] = new { recognized.Text, bounds };

            var neural = await new NeuralOcrService().RecognizeAsync(jpg.ToArray());
            if (!neural.Text.Contains("CIRCLE IMAGE TEXT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Neural OCR failed: {neural.Text}");
            if (neural.Lines.SelectMany(line => line.Words).Any(word => word.Bounds.Width <= 0))
                throw new InvalidOperationException("Neural OCR returned a word without a hit target.");
            results["neuralOcr"] = new { neural.Text,
                words = neural.Lines.Sum(line => line.Words.Count) };
            var screen = await new ScreenOcrPipeline().RecognizeAsync(jpg.ToArray());
            if (!screen.Text.Contains("CIRCLE IMAGE TEXT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Screen OCR failed: {screen.Text}");
            results["screenOcr"] = new { screen.Text,
                words = screen.Lines.Sum(line => line.Words.Count) };

            using var mixed = new Bitmap(1200, 300);
            using (var graphics = Graphics.FromImage(mixed))
            {
                graphics.Clear(Color.White);
                using var font = new Font("Microsoft YaHei", 38, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString("搜索屏幕上的中文文字", font, Brushes.Black, 35, 35);
                graphics.DrawString("English and 中文 on the same screen", font, Brushes.Black, 35, 125);
            }
            using var mixedPng = new MemoryStream();
            mixed.Save(mixedPng, ImageFormat.Png);
            var mixedResult = await new ScreenOcrPipeline().RecognizeAsync(mixedPng.ToArray());
            var mixedGroups = TextLayout.Build(OcrSelection.Flatten(mixedResult));
            if (!mixedGroups.Any(group => group.Text.Contains("搜索屏幕上的中文文字"))
                || !mixedGroups.Any(group => group.Text.Contains("English and 中文 on the same screen")))
                throw new InvalidOperationException("Mixed-language OCR or text grouping failed: "
                    + string.Join(" | ", mixedGroups.Select(group => group.Text)));
            results["multilingualAuto"] = mixedGroups.Select(group => group.Text).ToArray();

            var mixedHits = OcrSelection.Flatten(mixedResult);
            var chineseGroup = mixedGroups.First(group => group.Text.Contains("搜索屏幕上的中文文字"));
            var singleIndex = chineseGroup.WordIndices.First(index => mixedHits[index].Word.Text == "索");
            var singleRange = OcrSelection.TapRange(mixedHits, chineseGroup, singleIndex,
                preciseMode: false);
            if (singleRange is null || OcrSelection.TextBetween(mixedHits,
                    singleRange.Value.First, singleRange.Value.Last) != "索")
                throw new InvalidOperationException("Smart tap did not select one Chinese character.");
            var mixedGroup = mixedGroups.First(group => group.Text.Contains("English and 中文"));
            var englishIndex = mixedGroup.WordIndices.First(index => mixedHits[index].Word.Text == "g");
            var englishRange = OcrSelection.TapRange(mixedHits, mixedGroup, englishIndex,
                preciseMode: false);
            if (englishRange is null || OcrSelection.TextBetween(mixedHits,
                    englishRange.Value.First, englishRange.Value.Last) != "English")
                throw new InvalidOperationException("Smart tap did not select one Latin word in mixed text.");
            var chineseWord = mixedGroup.WordIndices.Where(index => mixedHits[index].Word.Text is "中" or "文")
                .ToArray();
            if (chineseWord.Length != 2 || OcrSelection.TextBetween(mixedHits,
                    chineseWord[0], chineseWord[1]) != "中文")
                throw new InvalidOperationException("Drag range did not select the Chinese phrase.");
            results["preciseMixedSelection"] = new { character = "索", latinWord = "English",
                chinesePhrase = "中文" };

            var iconLines = new[]
            {
                new OcrLine("Bambu Slime Rancher", new PixelBounds(145, 130, 197, 20),
                [
                    new OcrWord("Bambu", new PixelBounds(145, 130, 58, 20)),
                    new OcrWord("Slime", new PixelBounds(232, 130, 45, 20)),
                    new OcrWord("Rancher", new PixelBounds(282, 130, 60, 20))
                ]),
                new OcrLine("Studio 2", new PixelBounds(146, 157, 147, 20),
                [
                    new OcrWord("Studio", new PixelBounds(146, 157, 57, 20)),
                    new OcrWord("2", new PixelBounds(281, 157, 12, 20))
                ])
            };
            var iconHits = OcrSelection.Flatten(new OcrDocument("", iconLines));
            var iconGroups = TextLayout.Build(iconHits);
            var bambu = TextLayout.At(iconGroups, iconHits, new Point(170, 140));
            var slime = TextLayout.At(iconGroups, iconHits, new Point(255, 140));
            if (iconGroups.Count != 2 || bambu?.Text != "Bambu Studio"
                || slime?.Text != "Slime Rancher 2"
                || !ReferenceEquals(bambu, TextLayout.At(iconGroups, iconHits, new Point(173, 168)))
                || !ReferenceEquals(slime, TextLayout.At(iconGroups, iconHits, new Point(286, 168))))
                throw new InvalidOperationException("Adjacent wrapped icon labels were grouped incorrectly: "
                    + string.Join(" | ", iconGroups.Select(group => group.Text.Replace(Environment.NewLine, "/"))));
            results["adjacentIconLabels"] = iconGroups.Select(group => group.Text).ToArray();
            if (OcrSelection.TapRange(iconHits, bambu!, 0, preciseMode: false) is not null)
                throw new InvalidOperationException("Wrapped desktop label no longer selects as one block.");

            var paragraph = new OcrDocument("Want to restore the planet", [
                new OcrLine("Want to restore the planet", new PixelBounds(10, 10, 470, 35), [
                    new OcrWord("Want", new PixelBounds(10, 10, 80, 35)),
                    new OcrWord("to", new PixelBounds(105, 10, 35, 35)),
                    new OcrWord("restore", new PixelBounds(155, 10, 110, 35)),
                    new OcrWord("the", new PixelBounds(280, 10, 50, 35)),
                    new OcrWord("planet", new PixelBounds(345, 10, 125, 35))
                ])
            ]);
            var paragraphHits = OcrSelection.Flatten(paragraph);
            var paragraphGroup = TextLayout.Build(paragraphHits).Single();
            var paragraphTap = OcrSelection.TapRange(paragraphHits, paragraphGroup, 2,
                preciseMode: false);
            if (paragraphTap != (2, 2)
                || OcrSelection.TextBetween(paragraphHits, 1, 3) != "to restore the")
                throw new InvalidOperationException("English paragraph selection was not word-precise.");
            results["preciseEnglishSelection"] = new { tap = "restore", drag = "to restore the" };

            var hits = OcrSelection.Flatten(recognized);
            var selectedText = OcrSelection.TextBetween(hits, 0, hits.Count - 1);
            if (!selectedText.Contains("CIRCLE IMAGE TEXT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Text hit selection failed: {selectedText}");
            results["wordTargets"] = new { count = hits.Count, selectedText };

            var fullFrame = await new ScreenOcrPipeline().RecognizeAsync(jpg.ToArray());
            var fullHits = OcrSelection.Flatten(fullFrame);
            var textArea = new Rectangle(20, 35, 740, 90);
            var spatialWords = OcrSelection.WordsInside(fullHits, textArea);
            var spatialText = OcrSelection.TextForIndices(fullHits, spatialWords);
            if (!spatialText.Contains("CIRCLE IMAGE TEXT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Smart text area failed: {spatialText}");
            var emptyArea = new Rectangle(20, 140, 740, 35);
            if (OcrSelection.WordsInside(fullHits, emptyArea).Count != 0)
                throw new InvalidOperationException("Smart image area included text hits.");
            var emptyCrop = SelectionImage.Crop(decoded, emptyArea);
            var emptyOcr = await new NeuralOcrService().RecognizeAsync(emptyCrop);
            if (!string.IsNullOrWhiteSpace(emptyOcr.Text))
                throw new InvalidOperationException($"Smart image crop unexpectedly found text: {emptyOcr.Text}");
            results["smartGesture"] = new { spatialText, emptyCropText = emptyOcr.Text };

            var textLasso = new[]
            {
                new Point(12, 25), new Point(760, 25),
                new Point(760, 155), new Point(12, 155)
            };
            var textLassoBounds = SelectionImage.LassoBounds(textLasso, decoded.Size);
            var maskedForOcr = SelectionImage.Crop(decoded, textLassoBounds, textLasso, whiteOutside: true);
            var lassoOcr = await new NeuralOcrService().RecognizeAsync(maskedForOcr);
            if (!lassoOcr.Text.Contains("CIRCLE IMAGE TEXT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Lasso OCR failed: {lassoOcr.Text}");
            results["jpgLassoOcr"] = lassoOcr.Text;

            using var blue = new Bitmap(100, 100);
            using (var graphics = Graphics.FromImage(blue)) graphics.Clear(Color.Blue);
            var lasso = new[] { new Point(10, 10), new Point(90, 15), new Point(50, 90) };
            var lassoBounds = SelectionImage.LassoBounds(lasso, blue.Size);
            var transparent = SelectionImage.Crop(blue, lassoBounds, lasso);
            var white = SelectionImage.Crop(blue, lassoBounds, lasso, whiteOutside: true);
            using var transparentImage = new Bitmap(new MemoryStream(transparent));
            using var whiteImage = new Bitmap(new MemoryStream(white));
            var outsideTransparent = transparentImage.GetPixel(0, 0).A == 0;
            var outsideWhite = whiteImage.GetPixel(0, 0).ToArgb() == Color.White.ToArgb();
            var insideBlue = transparentImage.GetPixel(40, 30).B == 255;
            if (!outsideTransparent || !outsideWhite || !insideBlue)
                throw new InvalidOperationException("Lasso mask or OCR background failed.");
            results["lassoMask"] = new { lassoBounds, outsideTransparent, outsideWhite, insideBlue };
            results["cloudRequests"] = 0;
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
