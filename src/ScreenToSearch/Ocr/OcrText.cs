using System.Text;

namespace screen_to_search;

internal static class OcrText
{
    public static bool ContainsCjk(string text) => text.Any(IsCjk);

    public static string Join(IEnumerable<OcrWord> words)
    {
        var text = new StringBuilder();
        OcrWord? previous = null;
        foreach (var word in words)
        {
            if (previous is not null && (word.SpaceBefore == true ||
                word.SpaceBefore is null && NeedsSpace(previous.Text, word.Text) ||
                word.SpaceBefore == false && MissingPunctuationSpace(previous, word)))
                text.Append(' ');
            text.Append(word.Text);
            previous = word;
        }
        return text.ToString();
    }

    public static string JoinParts(IEnumerable<string> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts.Where(part => part.Length > 0))
        {
            if (text.Length > 0 && NeedsSpace(text.ToString(), part)) text.Append(' ');
            text.Append(part);
        }
        return text.ToString();
    }

    public static bool?[] SpacesFromLine(string line, IReadOnlyList<string> words)
    {
        var spaces = new bool?[words.Count];
        var offset = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var found = line.IndexOf(words[i], offset, StringComparison.Ordinal);
            if (found < 0) continue;
            spaces[i] = found > offset && string.IsNullOrWhiteSpace(line[offset..found]);
            offset = found + words[i].Length;
        }
        return spaces;
    }

    private static bool NeedsSpace(string left, string right)
    {
        var first = right[0];
        var last = left[^1];
        if (IsCjk(last) && IsCjk(first)) return false;
        if (char.IsPunctuation(first) || last is '(' or '[' or '{') return false;
        if (last is ('.' or ',') && char.IsDigit(first) && left.Length > 1
            && char.IsDigit(left[^2])) return false;
        return true;
    }

    private static bool MissingPunctuationSpace(OcrWord left, OcrWord right)
    {
        if (left.Text.Length == 0 || right.Text.Length == 0) return false;
        var last = left.Text[^1];
        var first = right.Text[0];
        if (last is not (',' or '.') || !char.IsLetterOrDigit(first)) return false;
        if (last == ',' && char.IsLetter(first)) return true;
        if (last == '.' && char.IsUpper(first)) return true;
        if (char.IsDigit(first) && left.Text.Length > 1 && char.IsDigit(left.Text[^2]))
            return false;
        var gap = right.Bounds.X - (left.Bounds.X + left.Bounds.Width);
        return gap >= Math.Max(1, Math.Min(left.Bounds.Height, right.Bounds.Height) * 0.12);
    }

    private static bool IsCjk(char character) => character is >= '\u2E80' and <= '\u9FFF'
        or >= '\uAC00' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF'
        or >= '\u3040' and <= '\u30FF';
}
