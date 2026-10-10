using System.Drawing;

namespace screen_to_search;

internal sealed class OcrDocumentBuilder
{
    private readonly List<OcrLine> _lines;
    private readonly List<OcrWord> _words;

    public OcrDocumentBuilder(OcrDocument baseline)
    {
        _lines = new List<OcrLine>(baseline.Lines);
        _words = baseline.Lines.SelectMany(line => line.Words).ToList();
    }

    public void Add(OcrDocument result, Rectangle area)
    {
        foreach (var line in result.Lines)
        {
            var accepted = new List<OcrWord>();
            foreach (var word in line.Words)
            {
                var bounds = word.Bounds;
                if (string.IsNullOrWhiteSpace(word.Text) || bounds.Width <= 0 || bounds.Height <= 0)
                    continue;
                var projected = new OcrWord(word.Text, new PixelBounds(
                    area.X + bounds.X, area.Y + bounds.Y, bounds.Width, bounds.Height));
                if (_words.Any(existing => SameLocation(existing.Bounds, projected.Bounds))
                    || CoveredByExistingWords(projected.Bounds)) continue;
                accepted.Add(projected);
                _words.Add(projected);
            }
            if (accepted.Count == 0) continue;
            var left = accepted.Min(word => word.Bounds.X);
            var top = accepted.Min(word => word.Bounds.Y);
            var right = accepted.Max(word => word.Bounds.X + word.Bounds.Width);
            var bottom = accepted.Max(word => word.Bounds.Y + word.Bounds.Height);
            _lines.Add(new OcrLine(OcrText.Join(accepted),
                new PixelBounds(left, top, right - left, bottom - top), accepted));
        }
    }

    public OcrDocument Build() => new(string.Join(Environment.NewLine, _lines.Select(line => line.Text)),
        _lines);

    private static bool SameLocation(PixelBounds first, PixelBounds second)
    {
        var overlapWidth = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width)
            - Math.Max(first.X, second.X));
        var overlapHeight = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height)
            - Math.Max(first.Y, second.Y));
        var smallerArea = Math.Min(first.Width * first.Height, second.Width * second.Height);
        return smallerArea > 0 && overlapWidth * overlapHeight / smallerArea >= 0.55;
    }

    private bool CoveredByExistingWords(PixelBounds candidate)
    {
        var area = candidate.Width * candidate.Height;
        if (area <= 0) return false;
        var covered = 0.0;
        foreach (var word in _words)
        {
            var bounds = word.Bounds;
            var width = Math.Max(0, Math.Min(bounds.X + bounds.Width, candidate.X + candidate.Width)
                - Math.Max(bounds.X, candidate.X));
            var height = Math.Max(0, Math.Min(bounds.Y + bounds.Height, candidate.Y + candidate.Height)
                - Math.Max(bounds.Y, candidate.Y));
            covered += width * height;
            if (covered >= area * 0.65) return true;
        }
        return false;
    }
}
