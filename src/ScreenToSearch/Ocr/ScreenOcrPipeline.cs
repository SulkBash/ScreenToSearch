namespace screen_to_search;

internal sealed class ScreenOcrPipeline
{
    private readonly NeuralOcrService _neural = new();

    public Task<OcrDocument> RecognizeAsync(byte[] image, string language,
        CancellationToken cancellationToken = default) =>
        _neural.RecognizeForLanguageAsync(image, language, cancellationToken, maxSide: 2048);
}
