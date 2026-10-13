using System.IO;
using RapidOcrNet;
using SkiaSharp;

namespace screen_to_search;

internal sealed class NeuralOcrService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RapidOcr? _engine;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    // One wake-up after the last request, rather than a recurring background poll.
    private static readonly System.Threading.Timer IdleTimer = new(
        _ => ReleaseIdleEngine(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private static void ReleaseIdleEngine()
    {
        // Recognition owns the same gate, so a timer can never dispose an engine in use.
        if (!Gate.Wait(0)) return;
        try
        {
            // A callback already queued before a new request may arrive late.
            var remaining = IdleTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastUse);
            if (remaining > TimeSpan.Zero)
            {
                IdleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            _engine?.Dispose();
            _engine = null;
        }
        finally { Gate.Release(); }
    }

    private static long _lastUse;

    public async Task<OcrDocument> RecognizeAsync(byte[] image, CancellationToken cancellationToken = default,
        int maxSide = 0)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IdleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            // Model initialization also belongs off the UI thread on a cold first use.
            return await Task.Run(() => RecognizeCoreAsync(image, maxSide, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lastUse = Environment.TickCount64;
            IdleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            Gate.Release();
        }
    }

    private static async Task<OcrDocument> RecognizeCoreAsync(byte[] image, int maxSide,
        CancellationToken cancellationToken)
    {
        var engine = GetEngine();
        using var bitmap = await Task.Run(() => SKBitmap.Decode(image), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The OCR image could not be decoded.");
        var options = RapidOcrOptions.PPOCRv6 with
        {
            ReturnWordBox = true
        };
        if (maxSide > 0) options = options with { ImgResize = maxSide };
        var result = await engine.DetectAsync(bitmap, options, null, cancellationToken)
            .ConfigureAwait(false);
        var lines = new List<OcrLine>();
        foreach (var block in result.TextBlocks)
        {
            var words = new List<OcrWord>();
            var wordResults = block.WordResults?.Where(word => !string.IsNullOrWhiteSpace(word.Text)
                && word.Score >= 0.3f).ToArray();
            var spaces = wordResults is null ? [] : OcrText.SpacesFromLine(block.Text,
                wordResults.Select(word => word.Text).ToArray());
            if (block.WordResults is not null)
                for (var i = 0; i < wordResults!.Length; i++)
                {
                    var word = wordResults[i];
                    var x0 = word.BoxPoints.Min(point => point.X);
                    var y0 = word.BoxPoints.Min(point => point.Y);
                    var x1 = word.BoxPoints.Max(point => point.X);
                    var y1 = word.BoxPoints.Max(point => point.Y);
                    words.Add(new OcrWord(word.Text, new PixelBounds(x0, y0, x1 - x0, y1 - y0),
                        spaces[i]));
                }
            if (words.Count == 0) continue;
            var left = words.Min(word => word.Bounds.X);
            var top = words.Min(word => word.Bounds.Y);
            var right = words.Max(word => word.Bounds.X + word.Bounds.Width);
            var bottom = words.Max(word => word.Bounds.Y + word.Bounds.Height);
            lines.Add(new OcrLine(block.Text,
                new PixelBounds(left, top, right - left, bottom - top), words));
        }
        var rows = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(line => line.Bounds.Y + line.Bounds.Height / 2))
        {
            var row = rows.FirstOrDefault(row => SameRow(row[0].Bounds, line.Bounds));
            if (row is null) rows.Add([line]);
            else row.Add(line);
        }
        var merged = rows.Select(row =>
        {
            var words = new List<OcrWord>();
            foreach (var (word, firstInBlock) in row.SelectMany(line =>
                         line.Words.Select((word, index) => (word, index == 0)))
                     .OrderBy(part => part.word.Bounds.X))
            {
                if (firstInBlock && words.Count > 0)
                {
                    var previous = words[^1];
                    var gap = word.Bounds.X - previous.Bounds.X - previous.Bounds.Width;
                    var threshold = Math.Max(2, Math.Min(previous.Bounds.Height, word.Bounds.Height) * 0.15);
                    var separateLatinWords = previous.Text.Length > 1 && word.Text.Length > 1
                        && previous.Text.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                        && word.Text.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
                    words.Add(word with { SpaceBefore = separateLatinWords || gap >= threshold });
                }
                else words.Add(word);
            }
            var left = words.Min(word => word.Bounds.X);
            var top = words.Min(word => word.Bounds.Y);
            var right = words.Max(word => word.Bounds.X + word.Bounds.Width);
            var bottom = words.Max(word => word.Bounds.Y + word.Bounds.Height);
            return new OcrLine(OcrText.Join(words),
                new PixelBounds(left, top, right - left, bottom - top), words);
        }).OrderBy(line => line.Bounds.Y).ToArray();
        return new OcrDocument(string.Join(Environment.NewLine, merged.Select(line => line.Text)), merged);
    }

    private static bool SameRow(PixelBounds first, PixelBounds second)
    {
        var overlap = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height)
            - Math.Max(first.Y, second.Y));
        var centerDistance = Math.Abs(first.Y + first.Height / 2 - second.Y - second.Height / 2);
        return overlap >= Math.Min(first.Height, second.Height) * 0.45
            && centerDistance <= Math.Max(first.Height, second.Height) * 0.7;
    }

    private static RapidOcr GetEngine() => _engine ??= CreateEngine();

    private static RapidOcr CreateEngine()
    {
        var engine = new RapidOcr();
        var models = Path.Combine(AppContext.BaseDirectory, "models");
        var set = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = Path.Combine(models, "v6", "PP-OCRv6_det_small.onnx"),
            RecModelPath = Path.Combine(models, "v6", "PP-OCRv6_rec_small.onnx"),
            KeysPath = Path.Combine(models, "v6", "ppocrv6_small_dict.txt")
        };
        try
        {
            using var options = RapidOcr.GetDefaultSessionOptions();
            // Screenshots vary in size; don't retain the largest inference's scratch arena.
            options.EnableCpuMemArena = false;
            // This is an occasional desktop task: let workers sleep between inference runs.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            engine.InitModels(set, options);
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }
}
