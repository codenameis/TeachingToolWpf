using System.Collections.Generic;
using TeachingToolWpf.Imaging.Matching;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

/// <summary>
/// Aggregated evaluation metrics for an OCR engine across a benchmark split (<c>all</c>, <c>tuning</c>, or <c>holdout</c>).
/// </summary>
public sealed record OcrEngineEvaluationReport(
    InspectionSuiteAlgorithmKind Engine,
    string SplitName,
    int TotalRois,
    int ExactMatchRois,
    int TotalEditDistance,
    int TotalReferenceChars,
    double CharacterErrorRate,
    double LineExactAccuracy,
    double RejectionRate,
    double MisreadPassRate,
    double ConfidenceAccuracyCorrelation,
    double MeanFirstCallLatencyMs,
    double MeanWarmCallLatencyMs,
    IReadOnlyList<OcrRoiEvaluationSample> Samples);
