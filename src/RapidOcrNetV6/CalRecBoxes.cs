// Apache-2.0 license
// Adapted from RapidAI / RapidOCR (python/rapidocr/cal_rec_boxes/main.py and ch_ppocr_rec/utils.py)
// https://github.com/RapidAI/RapidOCR

using SkiaSharp;

namespace RapidOcrNet;

internal enum WordType : byte
{
    Cn,
    EnNum
}

internal sealed class WordInfo
{
    public List<List<string>> Words { get; } = new();

    public List<List<int>> WordCols { get; } = new();

    public List<WordType> WordTypes { get; } = new();

    public List<List<float>> WordScores { get; } = new();

    /// <summary>
    /// Effective total column count (CTC time dimension), used for column→pixel mapping.
    /// </summary>
    public float LineTxtLen { get; internal set; }
}

internal static class CalRecBoxes
{
    private sealed class Cell(string text, float score, SKPoint[] points)
    {
        public string Text { get; } = text;
        public float Score { get; } = score;
        public SKPoint[] Points { get; } = points;
        public List<Cell>? Characters { get; init; }
    }

    /// <summary>
    /// Build per-word (or per-char) polygons for a single recognized text line and map them
    /// back to the original padded source-image coordinates.
    /// </summary>
    /// <param name="line">Recognizer output for this crop, with CharCols populated.</param>
    /// <param name="crop">Geometry bookkeeping from <see cref="OcrUtils.GetRotateCropImage(SKBitmap, SKPointI[], out CropContext)"/>.</param>
    /// <param name="cls180">Whether the angle classifier applied a 180° flip after the crop step.</param>
    /// <param name="returnSingleCharBox">If true, emit per-char polygons even for Latin/numeric lines.</param>
    /// <param name="returnWordCharacters">If true, retain character polygons within word boxes.</param>
    public static WordBox[]? Build(TextLine line, in CropContext crop, bool cls180,
        bool returnSingleCharBox, bool returnWordCharacters)
    {
        if (line.Chars is null || line.CharCols is null || line.CharScores is null
            || line.Chars.Length == 0 || line.ColCount <= 0)
        {
            return null;
        }

        string lineText = string.Concat(line.Chars);
        if (lineText.Length == 0)
        {
            return null;
        }

        // CJK and explicitly requested character boxes already allow selecting the
        // value independently, so leave their existing character geometry alone.
        WordInfo info = BuildWordInfo(line, !returnSingleCharBox && !HasChineseChar(lineText));
        if (info.Words.Count == 0)
        {
            return null;
        }

        // Recognized image dimensions (the image the recognizer actually saw, i.e. possibly
        // 90°-rotated partImg, possibly 180°-flipped by classifier).
        int recImgWidth = crop.Rotated90 ? crop.PartImgHeight : crop.PartImgWidth;
        int recImgHeight = crop.Rotated90 ? crop.PartImgWidth : crop.PartImgHeight;

        float avgColWidth = recImgWidth / info.LineTxtLen;

        bool isAllEnNum = info.WordTypes.All(t => t == WordType.EnNum);
        bool perWord = isAllEnNum && !returnSingleCharBox;

        // Compute avg char width (for char-cell horizontal extent).
        var charWidths = new List<float>();
        foreach (var wordCols in info.WordCols)
        {
            if (wordCols.Count <= 1)
            {
                continue;
            }

            float total = (wordCols[wordCols.Count - 1] - wordCols[0]) * avgColWidth;
            charWidths.Add(total / (wordCols.Count - 1));
        }

        float avgCharWidth = charWidths.Count > 0
            ? charWidths.Average()
            : (lineText.Length > 0 ? (float)recImgWidth / lineText.Length : avgColWidth);

        // Build cells in recognized-image coords.
        // Each cell is a rect [x0,0,x1,recImgHeight] expanded to 4 corners (TL, TR, BR, BL).
        var cellsWithText = new List<Cell>();
        for (int wIdx = 0; wIdx < info.Words.Count; wIdx++)
        {
            List<string> word = info.Words[wIdx];
            List<int> wordCols = info.WordCols[wIdx];
            List<float> wordScores = info.WordScores[wIdx];

            if (perWord)
            {
                var perCharCells = BuildCellsForWord(wordCols, avgCharWidth, avgColWidth, recImgWidth, recImgHeight);
                if (perCharCells.Count == 0)
                {
                    continue;
                }

                // Word-level bounding box of all per-char cells.
                float xmin = float.MaxValue, ymin = float.MaxValue;
                float xmax = float.MinValue, ymax = float.MinValue;
                foreach (var cell in perCharCells)
                {
                    foreach (var p in cell)
                    {
                        if (p.X < xmin)
                        {
                            xmin = p.X;
                        }

                        if (p.Y < ymin)
                        {
                            ymin = p.Y;
                        }

                        if (p.X > xmax)
                        {
                            xmax = p.X;
                        }

                        if (p.Y > ymax)
                        {
                            ymax = p.Y;
                        }
                    }
                }

                var wordCell = new SKPoint[]
                {
                    new(xmin, ymin), new(xmax, ymin), new(xmax, ymax), new(xmin, ymax)
                };

                string wordText = string.Concat(word);
                List<Cell>? characters = null;
                if (returnWordCharacters && word.Count > 1)
                {
                    characters = new List<Cell>(word.Count);
                    for (int c = 0; c < word.Count; c++)
                        characters.Add(new Cell(word[c], wordScores[c], perCharCells[c]));
                    AdjustBoxOverlap(characters);
                }
                cellsWithText.Add(new Cell(wordText, wordScores.Average(), wordCell)
                {
                    Characters = characters
                });
            }
            else
            {
                var perCharCells = BuildCellsForWord(wordCols, avgCharWidth, avgColWidth, recImgWidth, recImgHeight);
                for (int c = 0; c < perCharCells.Count && c < word.Count; c++)
                {
                    cellsWithText.Add(new Cell(word[c], wordScores[c], perCharCells[c]));
                }
            }
        }

        if (cellsWithText.Count == 0)
        {
            return null;
        }

        // Halve horizontal overlap between neighboring cells (after sorting left-to-right).
        cellsWithText.Sort((a, b) => a.Points[0].X.CompareTo(b.Points[0].X));
        AdjustBoxOverlap(cellsWithText);

        // Map each cell back to original padded-source coords.
        var result = new WordBox[cellsWithText.Count];
        var inverse = default(SKMatrix);
        var hasInverse = crop.HasPerspective && crop.PerspectiveMatrix.TryInvert(out inverse);
        for (int i = 0; i < cellsWithText.Count; i++)
        {
            result[i] = MapCell(cellsWithText[i], in crop, cls180, recImgWidth, recImgHeight,
                hasInverse, in inverse);
        }

        return result;
    }

    private static WordBox MapCell(Cell cell, in CropContext crop, bool cls180, int width, int height,
        bool hasInverse, in SKMatrix inverse)
    {
        var mapped = OrderPoints(ReverseMap(cell.Points, in crop, cls180, width, height,
            hasInverse, in inverse));
        WordBox[]? characters = null;
        if (cell.Characters is { } children)
        {
            characters = new WordBox[children.Count];
            for (var i = 0; i < children.Count; i++)
                characters[i] = MapCell(children[i], in crop, cls180, width, height, hasInverse, in inverse);
        }
        return new WordBox { Text = cell.Text, Score = cell.Score,
            BoxPoints = ToPointI(mapped), Characters = characters };
    }

    private static List<SKPoint[]> BuildCellsForWord(List<int> wordCols, float avgCharWidth, float avgColWidth,
        int recImgWidth, int recImgHeight)
    {
        var cells = new List<SKPoint[]>(wordCols.Count);
        foreach (int colIdx in wordCols)
        {
            float centerX = (colIdx + 0.5f) * avgColWidth;
            float x0 = MathF.Max(centerX - avgCharWidth / 2f, 0f);
            float x1 = MathF.Min(centerX + avgCharWidth / 2f, recImgWidth);
            if (x1 <= x0)
            {
                x1 = MathF.Min(x0 + 1, recImgWidth);
            }

            cells.Add([new SKPoint(x0, 0), new SKPoint(x1, 0), new SKPoint(x1, recImgHeight), new SKPoint(x0, recImgHeight)]);
        }

        cells.Sort((a, b) => a[0].X.CompareTo(b[0].X));
        return cells;
    }

    private static void AdjustBoxOverlap(List<Cell> cells)
    {
        for (int i = 0; i < cells.Count - 1; i++)
        {
            var cur = cells[i].Points;
            var nxt = cells[i + 1].Points;
            if (cur[1].X > nxt[0].X)
            {
                float distance = MathF.Abs(cur[1].X - nxt[0].X);
                float half = distance / 2f;
                cur[1].X -= half;
                cur[2].X -= half;
                nxt[0].X += distance - half;
                nxt[3].X += distance - half;
            }
        }
    }

    private static SKPoint[] ReverseMap(SKPoint[] cell, in CropContext crop, bool cls180, int recImgWidth,
        int recImgHeight, bool hasInverse, in SKMatrix inverse)
    {
        var mapped = new SKPoint[cell.Length];
        for (int i = 0; i < cell.Length; i++)
        {
            float rx = cell[i].X;
            float ry = cell[i].Y;

            // 1. Undo 180° classifier flip.
            if (cls180)
            {
                rx = recImgWidth - rx;
                ry = recImgHeight - ry;
            }

            // 2. Undo 90° CW pre-rotation applied by GetRotateCropImage.
            //    Forward: partImg pixel (sx, sy) → rotated pixel (H_partImg - sy, sx).
            //    Inverse: rotated (rx, ry) → partImg (ry, H_partImg - rx).
            //    Where H_partImg == recImgWidth (since rotation swapped W↔H).
            float px, py;
            if (crop.Rotated90)
            {
                px = ry;
                py = recImgWidth - rx;
            }
            else
            {
                px = rx;
                py = ry;
            }

            // 3. Undo perspective rectification: partImg → imgCrop.
            float cx, cy;
            if (hasInverse)
            {
                var q = inverse.MapPoint(new SKPoint(px, py));
                cx = q.X;
                cy = q.Y;
            }
            else
            {
                cx = px;
                cy = py;
            }

            // 4. Add (left, top) to return to original padded-source coords.
            mapped[i] = new SKPoint(cx + crop.Left, cy + crop.Top);
        }

        return mapped;
    }

    private static SKPointI[] ToPointI(SKPoint[] points)
    {
        var result = new SKPointI[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            result[i] = new SKPointI((int)MathF.Round(points[i].X), (int)MathF.Round(points[i].Y));
        }

        return result;
    }

    /// <summary>
    /// Sort the four corners of a (possibly rotated) quad in TL, TR, BR, BL order.
    /// Matches Python's CalRecBoxes.order_points behavior for typical (non-degenerate) cases.
    /// </summary>
    private static SKPoint[] OrderPoints(SKPoint[] pts)
    {
        if (pts.Length != 4)
        {
            return pts;
        }

        // Standard order-points heuristic: TL/BR by sum, TR/BL by diff.
        int tl = 0, tr = 0, br = 0, bl = 0;
        float minSum = float.MaxValue, maxSum = float.MinValue;
        float minDiff = float.MaxValue, maxDiff = float.MinValue;

        for (int i = 0; i < 4; i++)
        {
            float s = pts[i].X + pts[i].Y;
            float d = pts[i].Y - pts[i].X;
            if (s < minSum)
            {
                minSum = s;
                tl = i;
            }

            if (s > maxSum)
            {
                maxSum = s;
                br = i;
            }

            if (d < minDiff)
            {
                minDiff = d;
                tr = i;
            }

            if (d > maxDiff)
            {
                maxDiff = d;
                bl = i;
            }
        }

        return [pts[tl], pts[tr], pts[br], pts[bl]];
    }

    private static WordInfo BuildWordInfo(TextLine line, bool splitLabelValues)
    {
        var info = new WordInfo
        {
            // Prefer the batched-aware effective length set by TextRecognizer;
            // fall back to the raw column count for older call paths.
            LineTxtLen = line.LineTxtLen > 0 ? line.LineTxtLen : line.ColCount
        };

        string[] chars = line.Chars!;
        int[] cols = line.CharCols!;
        int n = chars.Length;
        if (n == 0) return info;

        // Per Python: colWidth[i] = cols[i] - cols[i-1], with a special first value.
        string firstChar = chars[0];
        int firstColWidthCap = HasChineseChar(firstChar) ? 3 : 2;
        int firstColWidth = Math.Min(firstColWidthCap, cols[0]);

        var currentWord = new List<string>();
        var currentCols = new List<int>();
        var currentScores = new List<float>();
        WordType? state = null;

        for (int i = 0; i < n; i++)
        {
            string ch = chars[i];

            if (string.IsNullOrEmpty(ch) || (ch.Length == 1 && char.IsWhiteSpace(ch[0])))
            {
                if (currentWord.Count > 0)
                {
                    info.Words.Add(currentWord);
                    info.WordCols.Add(currentCols);
                    info.WordScores.Add(currentScores);
                    info.WordTypes.Add(state ?? WordType.EnNum);
                    currentWord = new List<string>();
                    currentCols = new List<int>();
                    currentScores = new List<float>();
                }
                continue;
            }

            WordType cState = HasChineseChar(ch) ? WordType.Cn : WordType.EnNum;
            state ??= cState;

            int colWidth = i == 0 ? firstColWidth : (cols[i] - cols[i - 1]);
            if (state != cState || colWidth > 5
                || splitLabelValues && StartsLabelValue(currentWord, ch))
            {
                if (currentWord.Count > 0)
                {
                    info.Words.Add(currentWord);
                    info.WordCols.Add(currentCols);
                    info.WordScores.Add(currentScores);
                    info.WordTypes.Add(state.Value);
                    currentWord = new List<string>();
                    currentCols = new List<int>();
                    currentScores = new List<float>();
                }
                state = cState;
            }

            currentWord.Add(ch);
            currentCols.Add(cols[i]);
            currentScores.Add(line.CharScores![i]);
        }

        if (currentWord.Count > 0)
        {
            info.Words.Add(currentWord);
            info.WordCols.Add(currentCols);
            info.WordScores.Add(currentScores);
            info.WordTypes.Add(state ?? WordType.EnNum);
        }

        return info;
    }

    private static bool StartsLabelValue(List<string> word, string next)
    {
        if (word.Count < 2 || word[^1] is not (":" or "=") || next.Length == 0
            || !char.IsLetterOrDigit(next[0])) return false;

        // Keep label/value fields independently selectable even if OCR omits a space.
        // Numeric expressions such as 12:30 and URLs remain intact.
        var label = string.Concat(word.Take(word.Count - 1));
        return label.Any(char.IsLetter) && label.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool HasChineseChar(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (IsChineseChar(s[i]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsChineseChar(char c)
    {
        return (c >= '一' && c <= '鿿')   // CJK Unified Ideographs
            || (c >= '　' && c <= '〿')   // CJK punctuation
            || (c >= '＀' && c <= '￯');  // full-width forms
    }
}
