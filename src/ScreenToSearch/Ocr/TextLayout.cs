using System.Drawing;

namespace screen_to_search;

internal sealed record TextGroup(IReadOnlyList<int> WordIndices, Rectangle Bounds, string Text);

internal static class TextLayout
{
    private sealed class Segment
    {
        public required int Row { get; init; }
        public List<int> Words { get; } = [];
        public Rectangle Bounds { get; set; }
    }

    public static IReadOnlyList<TextGroup> Build(IReadOnlyList<OcrHit> hits)
    {
        if (hits.Count == 0) return [];
        var rows = new List<List<int>>();
        foreach (var index in Enumerable.Range(0, hits.Count)
                     .OrderBy(i => CenterY(Box(hits[i].Word))))
        {
            var box = Box(hits[index].Word);
            var row = rows.FindIndex(words => SameRow(Box(hits[words[0]].Word), box));
            if (row < 0) rows.Add([index]);
            else rows[row].Add(index);
        }

        var segments = new List<Segment>();
        for (var row = 0; row < rows.Count; row++)
        {
            Segment? current = null;
            foreach (var index in rows[row].OrderBy(i => Box(hits[i].Word).Left))
            {
                var box = Box(hits[index].Word);
                var gap = current is null ? int.MaxValue : box.Left - current.Bounds.Right;
                var maxGap = Math.Max(12, (int)Math.Round(Math.Min(box.Height,
                    current?.Bounds.Height ?? box.Height) * 0.75));
                if (current is null || gap > maxGap)
                {
                    current = new Segment { Row = row, Bounds = box };
                    segments.Add(current);
                }
                else current.Bounds = Rectangle.Union(current.Bounds, box);
                current.Words.Add(index);
            }
        }

        var parent = Enumerable.Range(0, segments.Count).ToArray();
        for (var row = 0; row < rows.Count - 1; row++)
        {
            var upper = Enumerable.Range(0, segments.Count).Where(i => segments[i].Row == row).ToArray();
            var lower = Enumerable.Range(0, segments.Count).Where(i => segments[i].Row == row + 1).ToArray();
            foreach (var first in upper)
            {
                var second = lower.Select(i => (Index: i, Score: LinkScore(segments[first].Bounds,
                        segments[i].Bounds)))
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
                if (second.Score <= 0) continue;
                var reverse = upper.Select(i => (Index: i, Score: LinkScore(segments[i].Bounds,
                        segments[second.Index].Bounds)))
                    .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
                if (reverse.Index == first) Union(parent, first, second.Index);
            }
        }

        return Enumerable.Range(0, segments.Count)
            .GroupBy(i => Root(parent, i))
            .Select(parts =>
            {
                var ordered = parts.Select(i => segments[i]).OrderBy(s => s.Row)
                    .ThenBy(s => s.Bounds.Left).ToArray();
                var indices = ordered.SelectMany(segment => segment.Words)
                    .Distinct().ToArray();
                var bounds = ordered.Select(segment => segment.Bounds).Aggregate(Rectangle.Union);
                var text = OcrText.JoinParts(ordered.Select(segment =>
                    OcrText.Join(segment.Words.Select(i => hits[i].Word))));
                return new TextGroup(indices, bounds, text);
            })
            .OrderBy(group => group.Bounds.Top).ThenBy(group => group.Bounds.Left).ToArray();
    }

    public static TextGroup? At(IReadOnlyList<TextGroup> groups, IReadOnlyList<OcrHit> hits, Point point)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            var box = Box(hits[i].Word);
            box.Inflate(3, 3);
            if (box.Contains(point)) return groups.FirstOrDefault(group => group.WordIndices.Contains(i));
        }
        return groups.Where(group =>
            {
                var box = group.Bounds;
                box.Inflate(3, 3);
                return box.Contains(point);
            })
            .MinBy(group => group.Bounds.Width * group.Bounds.Height);
    }

    public static IReadOnlyList<int> ExpandToGroups(IReadOnlyList<TextGroup> groups,
        IReadOnlyList<int> indices) => groups.Where(group => group.WordIndices.Any(indices.Contains))
        .SelectMany(group => group.WordIndices).Distinct().ToArray();

    public static string TextForGroups(IReadOnlyList<TextGroup> groups, IReadOnlyList<int> indices) =>
        string.Join(Environment.NewLine, groups.Where(group => group.WordIndices.Any(indices.Contains))
            .Select(group => group.Text));

    private static Rectangle Box(OcrWord word) => Rectangle.FromLTRB(
        (int)Math.Floor(word.Bounds.X), (int)Math.Floor(word.Bounds.Y),
        (int)Math.Ceiling(word.Bounds.X + word.Bounds.Width),
        (int)Math.Ceiling(word.Bounds.Y + word.Bounds.Height));

    private static double CenterY(Rectangle box) => box.Top + box.Height / 2.0;

    private static bool SameRow(Rectangle first, Rectangle second)
    {
        var overlap = Math.Max(0, Math.Min(first.Bottom, second.Bottom)
            - Math.Max(first.Top, second.Top));
        return overlap >= Math.Min(first.Height, second.Height) * 0.45
            && Math.Abs(CenterY(first) - CenterY(second)) <= Math.Max(first.Height, second.Height) * 0.7;
    }

    private static double LinkScore(Rectangle upper, Rectangle lower)
    {
        var gap = lower.Top - upper.Bottom;
        var maxGap = Math.Clamp(Math.Max(upper.Height, lower.Height) * 0.55, 6, 14);
        if (gap < -Math.Min(upper.Height, lower.Height) * 0.5 || gap > maxGap) return 0;
        var overlap = Math.Max(0, Math.Min(upper.Right, lower.Right)
            - Math.Max(upper.Left, lower.Left));
        var shared = overlap / (double)Math.Max(1, Math.Min(upper.Width, lower.Width));
        if (shared < 0.55) return 0;
        var centerDistance = Math.Abs((upper.Left + upper.Right) / 2.0
            - (lower.Left + lower.Right) / 2.0);
        return shared - centerDistance / (Math.Max(upper.Width, lower.Width) * 2.0);
    }

    private static int Root(int[] parent, int value)
    {
        while (parent[value] != value) value = parent[value] = parent[parent[value]];
        return value;
    }

    private static void Union(int[] parent, int first, int second) =>
        parent[Root(parent, second)] = Root(parent, first);
}
