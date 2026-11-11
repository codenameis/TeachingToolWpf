// File: src/VisionOcr.PpOcr/IPpOcrInferenceService.cs
using System.Collections.Generic;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionOcr.PpOcr;

/// <summary>
/// Lifetime-managed PP-OCRv4 ONNX inference service contract supporting both single-ROI and dynamic multi-ROI batched execution.
/// </summary>
public interface IPpOcrInferenceService : IDisposable
{
    PpOcrModelStatus Status { get; }

    string StatusMessage { get; }

    bool IsModelInstalled { get; }

    bool HasKoreanModel { get; }

    PpOcrExecutionDevice RequestedDevice { get; }

    PpOcrExecutionDevice ActiveDevice { get; }

    string? CudaFallbackReason { get; }

    int SessionCreationCount { get; }

    PpOcrRecognitionResult RecognizeRoi(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions? options = null);

    IReadOnlyList<PpOcrRecognitionResult> RecognizeBatch(
        IReadOnlyList<PpOcrRoiPatch> patches,
        PpOcrRunOptions? options = null)
    {
        var results = new PpOcrRecognitionResult[patches.Count];
        for (int i = 0; i < patches.Count; i++)
        {
            results[i] = patches[i].IsValid
                ? RecognizeRoi(patches[i].AsView(), options)
                : RecognizeRoi(default, options);
        }

        return results;
    }
}
