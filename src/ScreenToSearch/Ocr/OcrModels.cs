namespace screen_to_search;

internal sealed record PixelBounds(double X, double Y, double Width, double Height)
{
    public PixelBounds Offset(double x, double y) => this with { X = X + x, Y = Y + y };
}

internal sealed record OcrWord(string Text, PixelBounds Bounds, bool? SpaceBefore = null);
internal sealed record OcrLine(string Text, PixelBounds Bounds, IReadOnlyList<OcrWord> Words);
internal sealed record OcrDocument(string Text, IReadOnlyList<OcrLine> Lines);
