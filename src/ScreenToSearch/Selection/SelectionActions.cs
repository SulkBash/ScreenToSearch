using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace screen_to_search;

internal enum SelectionActionKind { SearchText, Translate, CopyText, CopyImage }
internal sealed record SelectionPayload(string Text, byte[] ImagePng);
internal sealed record SelectionActionResult(string Message, bool CloseSurface);

internal interface ISelectionActionProvider
{
    SelectionActionKind Kind { get; }
    SelectionActionResult Execute(SelectionPayload selection, AppSettings settings, Form owner);
}

internal static class SelectionActions
{
    private static readonly IReadOnlyDictionary<SelectionActionKind, ISelectionActionProvider> Providers =
        new ISelectionActionProvider[]
        {
            new SearchTextAction(), new TranslateTextAction(),
            new CopyTextAction(), new CopyImageAction()
        }.ToDictionary(provider => provider.Kind);

    public static SelectionActionResult Execute(SelectionActionKind kind, SelectionPayload selection,
        AppSettings settings, Form owner) => Providers[kind].Execute(selection, settings, owner);

    private static SelectionActionResult OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return new SelectionActionResult("Opened in your browser.", true);
    }

    private sealed class SearchTextAction : ISelectionActionProvider
    {
        public SelectionActionKind Kind => SelectionActionKind.SearchText;
        public SelectionActionResult Execute(SelectionPayload selection, AppSettings settings, Form owner)
        {
            if (string.IsNullOrWhiteSpace(selection.Text))
                return new SelectionActionResult("No text was recognized in this selection.", false);
            var query = Uri.EscapeDataString(selection.Text.Trim());
            var url = settings.SearchEngine switch
            {
                "bing" => $"https://www.bing.com/search?q={query}",
                "duckduckgo" => $"https://duckduckgo.com/?q={query}",
                _ => $"https://www.google.com/search?q={query}"
            };
            return OpenUrl(url);
        }
    }

    private sealed class TranslateTextAction : ISelectionActionProvider
    {
        public SelectionActionKind Kind => SelectionActionKind.Translate;
        public SelectionActionResult Execute(SelectionPayload selection, AppSettings settings, Form owner)
        {
            if (string.IsNullOrWhiteSpace(selection.Text))
                return new SelectionActionResult("No text was recognized in this selection.", false);
            return OpenUrl($"https://translate.google.com/?sl=auto&tl={Uri.EscapeDataString(settings.Language)}" +
                $"&text={Uri.EscapeDataString(selection.Text.Trim())}&op=translate");
        }
    }

    private sealed class CopyTextAction : ISelectionActionProvider
    {
        public SelectionActionKind Kind => SelectionActionKind.CopyText;
        public SelectionActionResult Execute(SelectionPayload selection, AppSettings settings, Form owner)
        {
            if (string.IsNullOrWhiteSpace(selection.Text))
                return new SelectionActionResult("No text was recognized in this selection.", false);
            Clipboard.SetText(selection.Text.Trim());
            return new SelectionActionResult("Text copied.", true);
        }
    }

    private sealed class CopyImageAction : ISelectionActionProvider
    {
        public SelectionActionKind Kind => SelectionActionKind.CopyImage;
        public SelectionActionResult Execute(SelectionPayload selection, AppSettings settings, Form owner)
        {
            using var stream = new MemoryStream(selection.ImagePng);
            using var decoded = new Bitmap(stream);
            using var copy = new Bitmap(decoded);
            Clipboard.SetImage(copy);
            return new SelectionActionResult("Image copied.", true);
        }
    }

}
