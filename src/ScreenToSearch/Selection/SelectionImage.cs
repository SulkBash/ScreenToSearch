using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace screen_to_search;

internal static class SelectionImage
{
    public static Rectangle RectangleBetween(Point first, Point last, Size imageSize)
    {
        var bounds = Rectangle.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
            Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
        return Rectangle.Intersect(new Rectangle(Point.Empty, imageSize), bounds);
    }

    public static Rectangle LassoBounds(IReadOnlyList<Point> points, Size imageSize)
    {
        if (points.Count < 3) return Rectangle.Empty;
        var bounds = Rectangle.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X) + 1, points.Max(p => p.Y) + 1);
        return Rectangle.Intersect(new Rectangle(Point.Empty, imageSize), bounds);
    }

    public static byte[] Crop(Bitmap source, Rectangle bounds, IReadOnlyList<Point>? lasso = null,
        bool whiteOutside = false)
    {
        bounds = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), bounds);
        if (bounds.Width < 1 || bounds.Height < 1) throw new ArgumentException("Selection has no pixels.");
        using var cropped = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(cropped))
        {
            graphics.Clear(whiteOutside ? Color.White : Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            if (lasso is { Count: >= 3 })
            {
                using var path = new GraphicsPath();
                path.AddPolygon(lasso.Select(p => new Point(p.X - bounds.X, p.Y - bounds.Y)).ToArray());
                graphics.SetClip(path);
            }
            graphics.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height),
                bounds, GraphicsUnit.Pixel);
        }
        using var stream = new MemoryStream();
        cropped.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public static Rectangle WordBounds(IEnumerable<OcrWord> words, Size imageSize)
    {
        var boxes = words.Select(word => word.Bounds).ToArray();
        if (boxes.Length == 0) return Rectangle.Empty;
        var left = (int)Math.Floor(boxes.Min(b => b.X)) - 4;
        var top = (int)Math.Floor(boxes.Min(b => b.Y)) - 4;
        var right = (int)Math.Ceiling(boxes.Max(b => b.X + b.Width)) + 4;
        var bottom = (int)Math.Ceiling(boxes.Max(b => b.Y + b.Height)) + 4;
        return Rectangle.Intersect(new Rectangle(Point.Empty, imageSize),
            Rectangle.FromLTRB(left, top, right, bottom));
    }
}

internal sealed record OcrHit(OcrWord Word, int LineIndex);
internal readonly record struct OcrTextPosition(int WordIndex, int CharacterIndex);
internal readonly record struct OcrTextRange(OcrTextPosition First, OcrTextPosition Last)
{
    public static OcrTextRange Between(OcrTextPosition first, OcrTextPosition last) =>
        first.WordIndex < last.WordIndex || first.WordIndex == last.WordIndex
            && first.CharacterIndex <= last.CharacterIndex
            ? new(first, last) : new(last, first);
}

// Keep mouse hit testing proportional to nearby words, even on text-heavy screens.
internal sealed class OcrHitIndex
{
    private const int CellSize = 96;
    private const int MaximumPadding = 30;
    private readonly IReadOnlyList<OcrHit> _hits;
    private readonly Dictionary<(int X, int Y), List<int>> _cells = [];

    public OcrHitIndex(IReadOnlyList<OcrHit> hits)
    {
        _hits = hits;
        for (var i = 0; i < hits.Count; i++)
        {
            var bounds = hits[i].Word.Bounds;
            var left = Cell(bounds.X - MaximumPadding);
            var right = Cell(bounds.X + bounds.Width + MaximumPadding);
            var top = Cell(bounds.Y - 3);
            var bottom = Cell(bounds.Y + bounds.Height + 3);
            for (var y = top; y <= bottom; y++)
                for (var x = left; x <= right; x++)
                {
                    if (!_cells.TryGetValue((x, y), out var indices))
                        _cells[(x, y)] = indices = [];
                    indices.Add(i);
                }
        }
    }

    public int WordAt(Point point, bool extendHorizontally = false) =>
        _cells.TryGetValue((Cell(point.X), Cell(point.Y)), out var indices)
            ? OcrSelection.WordAt(_hits, point, extendHorizontally, indices) : -1;

    private static int Cell(double coordinate) => (int)Math.Floor(coordinate / CellSize);
}

internal static class OcrSelection
{
    public static int WordAt(IReadOnlyList<OcrHit> hits, Point point, bool extendHorizontally = false,
        IReadOnlyList<int>? candidates = null)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        var bestCenterDistance = double.MaxValue;
        var count = candidates?.Count ?? hits.Count;
        for (var candidate = 0; candidate < count; candidate++)
        {
            var i = candidates is null ? candidate : candidates[candidate];
            var bounds = hits[i].Word.Bounds;
            var dx = Math.Max(0, Math.Max(bounds.X - point.X, point.X - bounds.X - bounds.Width));
            var dy = Math.Max(0, Math.Max(bounds.Y - point.Y, point.Y - bounds.Y - bounds.Height));
            var horizontalPadding = extendHorizontally ? Math.Clamp(bounds.Height * 0.75, 8, 30) : 3;
            if (dx > horizontalPadding || dy > 3) continue;
            var distance = dx * dx + dy * dy;
            var centerX = point.X - (bounds.X + bounds.Width / 2);
            var centerY = point.Y - (bounds.Y + bounds.Height / 2);
            var centerDistance = centerX * centerX + centerY * centerY;
            if (distance > bestDistance || distance == bestDistance && centerDistance >= bestCenterDistance)
                continue;
            best = i;
            bestDistance = distance;
            bestCenterDistance = centerDistance;
        }
        return best;
    }

    public static OcrTextPosition PositionAt(IReadOnlyList<OcrHit> hits, int wordIndex, Point point)
    {
        var word = hits[wordIndex].Word;
        if (word.Characters is not { Count: > 0 } characters) return new(wordIndex, 0);
        var bestIndex = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < characters.Count; i++)
        {
            var bounds = characters[i].Bounds;
            var dx = point.X - (bounds.X + bounds.Width / 2);
            var dy = point.Y - (bounds.Y + bounds.Height / 2);
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestIndex = i;
        }
        return new(wordIndex, bestIndex);
    }

    public static int CharacterCount(OcrWord word) =>
        word.Characters is { Count: > 0 } characters ? characters.Count : 1;

    public static OcrTextPosition MoveCharacter(IReadOnlyList<OcrHit> hits, OcrTextPosition position, int step)
    {
        var wordIndex = position.WordIndex;
        var characterIndex = position.CharacterIndex;
        if (step < 0)
        {
            if (characterIndex > 0) characterIndex--;
            else if (wordIndex > 0)
            {
                wordIndex--;
                characterIndex = CharacterCount(hits[wordIndex].Word) - 1;
            }
        }
        else if (characterIndex + 1 < CharacterCount(hits[wordIndex].Word)) characterIndex++;
        else if (wordIndex + 1 < hits.Count) { wordIndex++; characterIndex = 0; }
        return new OcrTextPosition(wordIndex, characterIndex);
    }

    public static PixelBounds SelectedBounds(OcrWord word, int first, int last)
    {
        if (word.Characters is not { Count: > 0 } characters
            || first == 0 && last == characters.Count - 1) return word.Bounds;
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        for (var i = first; i <= last; i++)
        {
            var bounds = characters[i].Bounds;
            left = Math.Min(left, bounds.X);
            top = Math.Min(top, bounds.Y);
            right = Math.Max(right, bounds.X + bounds.Width);
            bottom = Math.Max(bottom, bounds.Y + bounds.Height);
        }
        return new PixelBounds(left, top, right - left, bottom - top);
    }

    public static (int First, int Last) CharacterIndices(OcrTextRange range, int wordIndex,
        OcrWord word) => (wordIndex == range.First.WordIndex ? range.First.CharacterIndex : 0,
        wordIndex == range.Last.WordIndex ? range.Last.CharacterIndex : CharacterCount(word) - 1);

    public static IReadOnlyList<OcrWord> Slice(IReadOnlyList<OcrHit> hits, OcrTextRange range)
    {
        var words = new List<OcrWord>(range.Last.WordIndex - range.First.WordIndex + 1);
        for (var i = range.First.WordIndex; i <= range.Last.WordIndex; i++)
        {
            var word = hits[i].Word;
            var (first, last) = CharacterIndices(range, i, word);
            if (word.Characters is not { Count: > 0 } characters
                || first == 0 && last == characters.Count - 1)
            {
                words.Add(word);
                continue;
            }
            var text = string.Concat(characters.Skip(first).Take(last - first + 1)
                .Select(character => character.Text));
            words.Add(new OcrWord(text, SelectedBounds(word, first, last), word.SpaceBefore));
        }
        return words;
    }

    public static string TextForRange(IReadOnlyList<OcrHit> hits, OcrTextRange range,
        IReadOnlyList<OcrWord> words) => string.Join(Environment.NewLine,
        words.Select((word, index) => (Word: word, Line: hits[range.First.WordIndex + index].LineIndex))
            .GroupBy(part => part.Line)
            .Select(line => OcrText.Join(line.Select(part => part.Word))));

    public static (int First, int Last)? TapRange(IReadOnlyList<OcrHit> hits, TextGroup group,
        int wordIndex, bool preciseMode)
    {
        if (wordIndex < 0) return null;
        if (preciseMode || OcrText.ContainsCjk(hits[wordIndex].Word.Text))
            return (wordIndex, wordIndex);
        if (!OcrText.ContainsCjk(group.Text))
            return IsCompactWrappedLabel(hits, group) ? null : (wordIndex, wordIndex);
        if (!IsLatinCharacter(hits[wordIndex], hits[wordIndex]))
            return (wordIndex, wordIndex);
        var first = wordIndex;
        var last = wordIndex;
        while (first > 0 && hits[first].Word.SpaceBefore == false
            && IsLatinCharacter(hits[first - 1], hits[wordIndex])) first--;
        while (last + 1 < hits.Count && hits[last + 1].Word.SpaceBefore == false
            && IsLatinCharacter(hits[last + 1], hits[wordIndex])) last++;
        return (first, last);
    }

    private static bool IsCompactWrappedLabel(IReadOnlyList<OcrHit> hits, TextGroup group) =>
        group.WordIndices.Count is >= 2 and <= 4
        && group.WordIndices.Select(index => hits[index].LineIndex).Distinct().Count() is 2 or 3
        && group.Bounds.Width <= 150
        && group.Bounds.Height <= 100;

    private static bool IsLatinCharacter(OcrHit candidate, OcrHit origin) =>
        candidate.LineIndex == origin.LineIndex && candidate.Word.Text.Length == 1
        && char.IsLetterOrDigit(candidate.Word.Text[0])
        && !OcrText.ContainsCjk(candidate.Word.Text);

    public static IReadOnlyList<OcrHit> Flatten(OcrDocument document) =>
        document.Lines.SelectMany((line, index) => line.Words.Select(word => new OcrHit(word, index))).ToArray();

    public static string TextBetween(IReadOnlyList<OcrHit> hits, int first, int last)
    {
        if (hits.Count == 0) return "";
        first = Math.Clamp(first, 0, hits.Count - 1);
        last = Math.Clamp(last, 0, hits.Count - 1);
        if (first > last) (first, last) = (last, first);
        return string.Join(Environment.NewLine, hits.Skip(first).Take(last - first + 1)
            .GroupBy(hit => hit.LineIndex)
            .Select(line => OcrText.Join(line.Select(hit => hit.Word))));
    }

    public static IReadOnlyList<int> WordsInside(IReadOnlyList<OcrHit> hits, Rectangle area)
    {
        return Enumerable.Range(0, hits.Count).Where(index =>
        {
            var bounds = hits[index].Word.Bounds;
            var centerX = bounds.X + bounds.Width / 2;
            var centerY = bounds.Y + bounds.Height / 2;
            return area.Contains((int)Math.Round(centerX), (int)Math.Round(centerY));
        }).ToArray();
    }

    public static string TextForIndices(IReadOnlyList<OcrHit> hits, IReadOnlyList<int> indices) =>
        string.Join(Environment.NewLine, indices.OrderBy(index => index)
            .Select(index => hits[index])
            .GroupBy(hit => hit.LineIndex)
            .Select(line => OcrText.Join(line.Select(hit => hit.Word))));
}
