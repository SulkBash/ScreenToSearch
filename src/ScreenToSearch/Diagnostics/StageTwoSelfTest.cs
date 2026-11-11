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

            const string sentence = "one two three four five six seven eight nine ten";
            using var wordSample = new Bitmap(1100, 90);
            using (var graphics = Graphics.FromImage(wordSample))
            {
                graphics.Clear(Color.White);
                using var font = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString(sentence, font, Brushes.Black, 20, 15);
            }
            using var wordPng = new MemoryStream();
            wordSample.Save(wordPng, ImageFormat.Png);
            var sentenceHits = OcrSelection.Flatten(await new ScreenOcrPipeline().RecognizeAsync(wordPng.ToArray()));
            if (!sentenceHits.Select(hit => hit.Word.Text).SequenceEqual(sentence.Split(' ')))
                throw new InvalidOperationException("OCR merged separate words into a single selection target: "
                    + string.Join(" | ", sentenceHits.Select(hit => hit.Word.Text)));
            var sentenceGroups = TextLayout.Build(sentenceHits);
            var fourth = sentenceGroups.Single(group => group.WordIndices.Contains(3));
            if (OcrSelection.TapRange(sentenceHits, fourth, 3, preciseMode: false) != (3, 3)
                || OcrSelection.TextBetween(sentenceHits, 3, 5) != "four five six")
                throw new InvalidOperationException("Smart word selection expanded beyond the intended words.");
            results["separateWordTargets"] = sentenceHits.Count;

            // Some fonts cause OCR to omit the space after the label. Exercise the
            // real word boxes, including an explicitly unspaced label and value.
            const string accountNumber = "123456789123456789";
            using var labels = new Bitmap(900, 360);
            using (var graphics = Graphics.FromImage(labels))
            {
                graphics.Clear(Color.White);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var tahoma = new Font("Tahoma", 28, FontStyle.Regular, GraphicsUnit.Pixel);
                using var arial = new Font("Arial", 24, FontStyle.Regular, GraphicsUnit.Pixel);
                using var largeArial = new Font("Arial", 30, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString($"CLABE: {accountNumber}", tahoma, Brushes.Black, 20, 10);
                graphics.DrawString($"CLABE:{accountNumber}", arial, Brushes.Black, 20, 75);
                graphics.DrawString("ORDER:ABC123", largeArial, Brushes.Black, 20, 140);
                graphics.DrawString("12:30", largeArial, Brushes.Black, 20, 205);
                graphics.DrawString("https://example.com", largeArial, Brushes.Black, 20, 270);
            }
            using var labelPng = new MemoryStream();
            labels.Save(labelPng, ImageFormat.Png);
            var labelDocument = await new ScreenOcrPipeline().RecognizeAsync(labelPng.ToArray());
            var labelHits = OcrSelection.Flatten(labelDocument);
            var labelGroups = TextLayout.Build(labelHits);
            if (labelDocument.Lines.Count != 5)
                throw new InvalidOperationException($"Label OCR returned unexpected lines: {labelDocument.Text}");
            foreach (var lineIndex in new[] { 0, 1 })
            {
                var valueIndices = Enumerable.Range(0, labelHits.Count).Where(index =>
                    labelHits[index].LineIndex == lineIndex && labelHits[index].Word.Text == accountNumber).ToArray();
                if (valueIndices.Length != 1)
                    throw new InvalidOperationException($"Label and account number share a hit target: {labelDocument.Text}");
                var valueIndex = valueIndices[0];
                var group = labelGroups.Single(candidate => candidate.WordIndices.Contains(valueIndex));
                foreach (var preciseMode in new[] { false, true })
                {
                    var range = OcrSelection.TapRange(labelHits, group, valueIndex, preciseMode);
                    if (range is null || OcrSelection.TextBetween(labelHits,
                            range.Value.First, range.Value.Last) != accountNumber)
                        throw new InvalidOperationException("Tapping the account number included its label.");
                }
            }
            if (labelDocument.Lines[1].Text != $"CLABE:{accountNumber}"
                || !labelDocument.Lines[2].Words.Select(word => word.Text).SequenceEqual(["ORDER:", "ABC123"])
                || !labelDocument.Lines[3].Words.Select(word => word.Text).SequenceEqual(["12:30"])
                || !labelDocument.Lines[4].Words.Select(word => word.Text).SequenceEqual(["https://example.com"]))
                throw new InvalidOperationException($"Colon-separated text changed unexpectedly: {labelDocument.Text}");
            results["colonSeparatedSelection"] = new { accountNumber, fullText = labelDocument.Lines[1].Text };

            var numberIndex = Enumerable.Range(0, labelHits.Count).Single(index =>
                labelHits[index].LineIndex == 1 && labelHits[index].Word.Text == accountNumber);
            var labelIndex = Enumerable.Range(0, labelHits.Count).Single(index =>
                labelHits[index].LineIndex == 1 && labelHits[index].Word.Text == "CLABE:");
            var urlIndex = Enumerable.Range(0, labelHits.Count).Single(index =>
                labelHits[index].LineIndex == 4 && labelHits[index].Word.Text == "https://example.com");
            var numberWord = labelHits[numberIndex].Word;
            var urlWord = labelHits[urlIndex].Word;
            if (numberWord.Characters?.Count != accountNumber.Length
                || urlWord.Characters?.Count != urlWord.Text.Length)
                throw new InvalidOperationException("OCR did not return character positions inside words.");
            for (var i = 0; i < numberWord.Characters.Count; i++)
                CheckCharacterHit(labelHits, numberIndex, i);
            for (var i = 0; i < urlWord.Characters.Count; i++)
                CheckCharacterHit(labelHits, urlIndex, i);
            var partialNumber = OcrTextRange.Between(new(numberIndex, 2), new(numberIndex, 8));
            var reverseNumber = OcrTextRange.Between(new(numberIndex, 8), new(numberIndex, 2));
            var partialUrl = OcrTextRange.Between(new(urlIndex, 8), new(urlIndex, 14));
            var crossWords = OcrTextRange.Between(new(labelIndex, 3), new(numberIndex, 4));
            if (partialNumber != reverseNumber
                || RangeText(labelHits, partialNumber) != accountNumber.Substring(2, 7)
                || RangeText(labelHits, partialUrl) != "example"
                || RangeText(labelHits, crossWords) != "BE:12345")
                throw new InvalidOperationException("Character selection did not preserve the intended text.");

            var screenHits = OcrSelection.Flatten(screen);
            var circleIndex = Enumerable.Range(0, screenHits.Count).Single(index => screenHits[index].Word.Text == "CIRCLE");
            var imageIndex = Enumerable.Range(0, screenHits.Count).Single(index => screenHits[index].Word.Text == "IMAGE");
            var spacedRange = OcrTextRange.Between(new(circleIndex, 3), new(imageIndex, 2));
            if (RangeText(screenHits, spacedRange) != "CLE IMA")
                throw new InvalidOperationException("Character selection lost the space between words.");
            var lastOfCircle = new OcrTextPosition(circleIndex,
                OcrSelection.CharacterCount(screenHits[circleIndex].Word) - 1);
            var firstOfImage = OcrSelection.MoveCharacter(screenHits, lastOfCircle, 1);
            if (firstOfImage != new OcrTextPosition(imageIndex, 0)
                || OcrSelection.MoveCharacter(screenHits, firstOfImage, -1) != lastOfCircle
                || RangeText(screenHits, OcrTextRange.Between(lastOfCircle, firstOfImage)) != "E I")
                throw new InvalidOperationException("Character navigation failed across a word boundary.");
            var projected = new OcrDocumentBuilder(new OcrDocument("", []));
            projected.Add(new OcrDocument("", [labelDocument.Lines[1]]), new Rectangle(30, 40, 900, 360));
            var projectedNumber = projected.Build().Lines.Single().Words.Single(word => word.Text == accountNumber);
            if (projectedNumber.Characters is null || projectedNumber.SpaceBefore != numberWord.SpaceBefore
                || projectedNumber.Characters[0].Bounds.X != numberWord.Characters[0].Bounds.X + 30
                || projectedNumber.Characters[0].Bounds.Y != numberWord.Characters[0].Bounds.Y + 40)
                throw new InvalidOperationException("Small-text OCR lost character coordinates or spacing.");
            var adjacentHits = OcrSelection.Flatten(new OcrDocument("", [new OcrLine("AB",
                new PixelBounds(10, 10, 20, 10), [new OcrWord("A", new PixelBounds(10, 10, 10, 10)),
                    new OcrWord("B", new PixelBounds(20, 10, 10, 10))])]));
            if (OcrSelection.WordAt(adjacentHits, new Point(21, 15)) != 1)
                throw new InvalidOperationException("An inflated neighboring hit target hid the intended word.");
            var hitIndex = new OcrHitIndex(adjacentHits);
            for (var x = 0; x < 50; x++)
                for (var y = 5; y < 25; y++)
                    foreach (var extend in new[] { false, true })
                        if (hitIndex.WordAt(new Point(x, y), extend)
                            != OcrSelection.WordAt(adjacentHits, new Point(x, y), extend))
                            throw new InvalidOperationException("Indexed hit testing changed the selected word.");
            if (hitIndex.WordAt(new Point(36, 15), extendHorizontally: true) != 1)
                throw new InvalidOperationException("A drag ending just after the text lost its endpoint.");
            var edgeHits = new[]
            {
                new OcrHit(new OcrWord("A", new PixelBounds(-5, -3, 12, 15)), 0),
                new OcrHit(new OcrWord("B", new PixelBounds(90, 90, 20, 20)), 1),
                new OcrHit(new OcrWord("C", new PixelBounds(192, 96, 12, 20)), 1)
            };
            var edgeIndex = new OcrHitIndex(edgeHits);
            for (var x = -40; x <= 230; x += 3)
                for (var y = -20; y <= 130; y += 5)
                    foreach (var extend in new[] { false, true })
                        if (edgeIndex.WordAt(new Point(x, y), extend)
                            != OcrSelection.WordAt(edgeHits, new Point(x, y), extend))
                            throw new InvalidOperationException("A hit target was lost at a spatial index boundary.");
            VerifySurfaceGestures(labelDocument, labelPng.ToArray(), numberIndex, urlIndex);
            VerifyCharacterScores();
            results["characterSelection"] = new { partialNumber = RangeText(labelHits, partialNumber),
                partialUrl = RangeText(labelHits, partialUrl), acrossWords = RangeText(screenHits, spacedRange) };

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

    private static string RangeText(IReadOnlyList<OcrHit> hits, OcrTextRange range) =>
        OcrSelection.TextForRange(hits, range, OcrSelection.Slice(hits, range));

    private static void CheckCharacterHit(IReadOnlyList<OcrHit> hits, int wordIndex, int characterIndex)
    {
        var point = CharacterCenter(hits[wordIndex].Word, characterIndex);
        if (OcrSelection.WordAt(hits, point) != wordIndex
            || OcrSelection.PositionAt(hits, wordIndex, point) != new OcrTextPosition(wordIndex, characterIndex))
            throw new InvalidOperationException($"Character hit testing failed at {wordIndex}:{characterIndex}.");
    }

    private static Point CharacterCenter(OcrWord word, int index)
    {
        var bounds = word.Characters![index].Bounds;
        return new Point((int)Math.Round(bounds.X + bounds.Width / 2),
            (int)Math.Round(bounds.Y + bounds.Height / 2));
    }

    private static void VerifySurfaceGestures(OcrDocument document, byte[] image, int numberIndex, int urlIndex)
    {
        var monitor = new MonitorInfo("test", new Rectangle(0, 0, 900, 360), true, 96, 96, 0);
        using var surface = new CaptureSurface(new MonitorFrame(monitor, image, TimeSpan.Zero), new AppSettings());
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(CaptureSurface);
        type.GetField("_lines", flags)!.SetValue(surface, document.Lines);
        type.GetMethod("RebuildTextTargets", flags)!.Invoke(surface, null);
        var down = type.GetMethod("OnMouseDown", flags)!;
        var move = type.GetMethod("OnMouseMove", flags)!;
        var up = type.GetMethod("OnMouseUp", flags)!;
        var selection = type.GetField("_selection", flags)!;
        string CurrentText()
        {
            var selected = selection.GetValue(surface)
                ?? throw new InvalidOperationException("The overlay did not select text.");
            return (string)selected.GetType().GetProperty("Text")!.GetValue(selected)!;
        }
        string Gesture(Point first, Point last)
        {
            down.Invoke(surface, [new System.Windows.Forms.MouseEventArgs(
                System.Windows.Forms.MouseButtons.Left, 1, first.X, first.Y, 0)]);
            up.Invoke(surface, [new System.Windows.Forms.MouseEventArgs(
                System.Windows.Forms.MouseButtons.Left, 1, last.X, last.Y, 0)]);
            return CurrentText();
        }

        var hits = OcrSelection.Flatten(document);
        var number = hits[numberIndex].Word;
        var url = hits[urlIndex].Word;
        var first = CharacterCenter(number, 2);
        var last = CharacterCenter(number, 8);
        down.Invoke(surface, [new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left, 1, first.X, first.Y, 0)]);
        move.Invoke(surface, [new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left, 0, last.X, last.Y, 0)]);
        using (var preview = new Bitmap(900, 360))
        using (var graphics = Graphics.FromImage(preview))
        {
            type.GetMethod("OnPaint", flags)!.Invoke(surface,
                [new System.Windows.Forms.PaintEventArgs(graphics, new Rectangle(0, 0, 900, 360))]);
            var selectedPoint = CharacterCenter(number, 0);
            var pixel = preview.GetPixel(selectedPoint.X, selectedPoint.Y);
            if (pixel.B <= pixel.R)
                throw new InvalidOperationException("Smart mode did not preview the whole word.");
        }
        up.Invoke(surface, [new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left, 1, last.X, last.Y, 0)]);
        var label = hits[numberIndex - 1].Word;
        if (Gesture(first, last) != number.Text || Gesture(last, first) != number.Text
            || Gesture(first, first) != number.Text
            || Gesture(CharacterCenter(url, 8), CharacterCenter(url, 14)) != url.Text
            || Gesture(CharacterCenter(label, 3), CharacterCenter(number, 4)) != "CLABE:" + number.Text)
            throw new InvalidOperationException("Smart mode did not select whole words.");
        type.GetMethod("SetMode", flags)!.Invoke(surface, [SelectionMode.Text]);
        if (Gesture(first, last) != "3456789" || Gesture(last, first) != "3456789"
            || Gesture(CharacterCenter(url, 8), CharacterCenter(url, 14)) != "example")
            throw new InvalidOperationException("Text mode did not select the intended characters.");
        if (Gesture(first, first) != "3")
            throw new InvalidOperationException("Text mode did not select one character on tap.");
        var keyDown = type.GetMethod("OnKeyDown", flags)!;
        keyDown.Invoke(surface, [new System.Windows.Forms.KeyEventArgs(
            System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Right)]);
        if (CurrentText() != "34")
            throw new InvalidOperationException("Shift and arrow did not extend character selection.");
        keyDown.Invoke(surface, [new System.Windows.Forms.KeyEventArgs(
            System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Right)]);
        if (CurrentText() != "345")
            throw new InvalidOperationException("Keyboard selection did not retain its anchor.");
        keyDown.Invoke(surface, [new System.Windows.Forms.KeyEventArgs(
            System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Left)]);
        if (CurrentText() != "34")
            throw new InvalidOperationException("Keyboard selection did not contract toward its anchor.");
    }

    private static void VerifyCharacterScores()
    {
        var assembly = typeof(RapidOcrNet.TextLine).Assembly;
        var cropType = assembly.GetType("RapidOcrNet.CropContext")!;
        var crop = Activator.CreateInstance(cropType, 0, 0, 120, 30, SkiaSharp.SKMatrix.Identity, false, false)!;
        var build = assembly.GetType("RapidOcrNet.CalRecBoxes")!.GetMethod("Build")!;
        var line = new RapidOcrNet.TextLine
        {
            Chars = ["A", " ", "B", "C"],
            CharScores = [0.91f, 0.01f, 0.8f, 0.6f],
            CharCols = [2, 4, 6, 8],
            ColCount = 20
        };
        var boxes = (RapidOcrNet.WordBox[])build.Invoke(null, [line, crop, false, false, true])!;
        if (boxes.Length != 2 || boxes[1].Text != "BC" || Math.Abs(boxes[1].Score - 0.7f) > 0.001f
            || boxes[1].Characters is not { Length: 2 } characters
            || Math.Abs(characters[0].Score - 0.8f) > 0.001f
            || Math.Abs(characters[1].Score - 0.6f) > 0.001f)
            throw new InvalidOperationException("Character confidence scores shifted after whitespace.");
        const string field = "account=123456789";
        var joined = new RapidOcrNet.TextLine
        {
            Chars = field.Select(character => character.ToString()).ToArray(),
            CharScores = Enumerable.Repeat(0.9f, field.Length).ToArray(),
            CharCols = Enumerable.Range(1, field.Length).Select(index => index * 2).ToArray(),
            ColCount = field.Length * 2 + 2
        };
        boxes = (RapidOcrNet.WordBox[])build.Invoke(null, [joined, crop, false, false, true])!;
        if (!boxes.Select(box => box.Text).SequenceEqual(["account=", "123456789"]))
            throw new InvalidOperationException("A joined label and value did not produce separate word targets.");
    }
}
