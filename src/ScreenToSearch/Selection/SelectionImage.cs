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

internal static class OcrSelection
{
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
