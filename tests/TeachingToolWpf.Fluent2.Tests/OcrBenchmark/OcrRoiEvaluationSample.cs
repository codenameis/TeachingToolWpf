namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

/// <summary>
/// Per-ROI evaluation measurement for a single OCR engine on a single <see cref="OcrBenchmarkEntry"/>.
/// </summary>
public sealed record OcrRoiEvaluationSample(
    string EntryId,
    string ImageFile,
    string Category,
    string Split,
    string GroundTruthText,
    string PredictedText,
    bool IsExactMatch,
    bool IsPassVerdict,
    bool IsRejected,
    bool IsMisreadPass,
    int EditDistance,
    int ReferenceLength,
    double CharacterErrorRate,
    double MeanConfidence,
    double MinConfidence,
    double FirstCallLatencyMs,
    double WarmCallLatencyMs);
