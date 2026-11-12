using System.Collections.Immutable;

namespace VisionOcr.PpOcr;

/// <summary>
/// Complete recognition output from <see cref="IPpOcrInferenceService"/>, including model status and softmax probabilities.
/// </summary>
public sealed record PpOcrRecognitionResult(
    PpOcrModelStatus ModelStatus,
    string StatusMessage,
    PpOcrRegionResult Region,
    ImmutableArray<PpOcrBoxResult> Boxes,
    PpOcrExecutionDevice ActiveDevice,
    bool UsedDetectionStage,
    bool UsedInvertedPolarity);
