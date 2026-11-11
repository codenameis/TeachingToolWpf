using VisionOcr.PpOcr;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

public sealed record PpOcrEvaluationSummary(
    OcrEngineEvaluationReport Report,
    PpOcrExecutionDevice RequestedDevice,
    PpOcrExecutionDevice ActiveDevice,
    string? CudaFallbackReason,
    double SessionInitMs,
    int SessionCreationCount);
