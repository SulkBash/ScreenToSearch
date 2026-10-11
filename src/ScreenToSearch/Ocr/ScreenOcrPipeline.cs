namespace screen_to_search;

internal sealed class ScreenOcrPipeline
{
    private readonly NeuralOcrService _neural = new();

    public Task<OcrDocument> RecognizeAsync(byte[] image,
        CancellationToken cancellationToken = default) =>
        _neural.RecognizeAsync(image, cancellationToken, maxSide: 2048);
}
