using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>순차 단계의 wall time. 각 단계에는 해당 단계의 원본 읽기와 병렬 worker 대기가 포함된다.</summary>
public sealed record ShapeSearchPhases(double SourceOpenMilliseconds, double CoarseSearchMilliseconds,
    double OriginalRefinementMilliseconds, double WindowSearchMilliseconds, double FinalSuppressionMilliseconds);
