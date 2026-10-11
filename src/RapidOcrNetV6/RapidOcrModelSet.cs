// Apache-2.0 license. Based on RapidOcrNet 4.2.0.
using System.IO;

namespace RapidOcrNet;

/// <summary>The PP-OCRv6 detector, recognizer, and dictionary.</summary>
public sealed record RapidOcrModelSet
{
    public required string DetModelPath { get; init; }
    public required string RecModelPath { get; init; }
    public required string KeysPath { get; init; }
    public required float[] DetMean { get; init; }
    public required float[] DetStd { get; init; }

    public static RapidOcrModelSet PPOCRv6Small { get; } = new()
    {
        DetModelPath = Path.Combine(RapidOcr.ModelsFolderName, "v6", "PP-OCRv6_det_small.onnx"),
        RecModelPath = Path.Combine(RapidOcr.ModelsFolderName, "v6", "PP-OCRv6_rec_small.onnx"),
        KeysPath = Path.Combine(RapidOcr.ModelsFolderName, "v6", "ppocrv6_small_dict.txt"),
        DetMean = [127.5F, 127.5F, 127.5F],
        DetStd = [127.5F, 127.5F, 127.5F]
    };
}
