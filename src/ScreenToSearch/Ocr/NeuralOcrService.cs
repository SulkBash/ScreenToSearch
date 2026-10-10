using System.IO;
using RapidOcrNet;
using SkiaSharp;

namespace screen_to_search;

internal sealed class NeuralOcrService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RapidOcr? _engine;
    private static RapidOcr? _multilingualEngine;

    public static bool SupportsLanguage(string language) => language is "auto" or "en" or "es" or "fr"
        or "de" or "pt" or "it" or "zh-CN" or "ja";

    public static bool UsesMultilingualModel(string language) => language is "auto" or "zh-CN" or "ja";

    public static async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try { await Task.Run(() => GetEngine(multilingual: false), cancellationToken); }
        finally { Gate.Release(); }
    }

    public static async Task WarmUpMultilingualAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try { await Task.Run(() => GetEngine(multilingual: true), cancellationToken); }
        finally { Gate.Release(); }
    }

    public async Task<OcrDocument> RecognizeForLanguageAsync(byte[] image, string language,
        CancellationToken cancellationToken = default, int maxSide = 0)
    {
        if (!UsesMultilingualModel(language)) return await RecognizeAsync(image, cancellationToken,
            maxSide > 0 ? maxSide : 1024);
        try { return await RecognizeMultilingualAsync(image, cancellationToken, maxSide); }
        catch (OperationCanceledException) { throw; }
        catch when (language == "auto") { return await RecognizeAsync(image, cancellationToken,
            maxSide > 0 ? maxSide : 1024); }
    }

    public async Task<OcrDocument> RecognizeAsync(byte[] image, CancellationToken cancellationToken = default,
        int maxSide = 1024)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecognizeCoreAsync(image, maxSide, multilingual: false, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task<OcrDocument> RecognizeMultilingualAsync(byte[] image,
        CancellationToken cancellationToken = default, int maxSide = 0)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecognizeCoreAsync(image, maxSide, multilingual: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static async Task<OcrDocument> RecognizeCoreAsync(byte[] image, int maxSide,
        bool multilingual, CancellationToken cancellationToken)
    {
        var engine = GetEngine(multilingual);
        using var bitmap = await Task.Run(() => SKBitmap.Decode(image), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The OCR image could not be decoded.");
        var options = (multilingual ? RapidOcrOptions.PPOCRv6 : RapidOcrOptions.Default) with
        {
            ReturnWordBox = true, DoAngle = false
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
        return new OcrDocument(string.Join(Environment.NewLine, lines.Select(line => line.Text)), lines);
    }

    private static RapidOcr GetEngine(bool multilingual)
    {
        if (multilingual)
        {
            var engine = _multilingualEngine ??= CreateMultilingualEngine();
            _engine?.Dispose();
            _engine = null;
            return engine;
        }
        else
        {
            var engine = _engine ??= CreateEngine();
            _multilingualEngine?.Dispose();
            _multilingualEngine = null;
            return engine;
        }
    }

    private static RapidOcr CreateEngine()
    {
        var engine = new RapidOcr();
        var models = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        engine.InitModels(
            Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(models, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            Path.Combine(models, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(models, "ppocrv5_latin_dict.txt"));
        return engine;
    }

    private static RapidOcr CreateMultilingualEngine()
    {
        var engine = new RapidOcr();
        var models = Path.Combine(AppContext.BaseDirectory, "models");
        var set = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = Path.Combine(models, "v6", "PP-OCRv6_det_small.onnx"),
            ClsModelPath = Path.Combine(models, "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            RecModelPath = Path.Combine(models, "v6", "PP-OCRv6_rec_small.onnx"),
            KeysPath = Path.Combine(models, "v6", "ppocrv6_small_dict.txt")
        };
        engine.InitModels(set);
        return engine;
    }
}
