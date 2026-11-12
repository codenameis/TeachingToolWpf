using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

public sealed record PrimitiveDetectionOptions
{
    public bool UseGpu
    {
        get; init;
    }
    public int MaximumParallelism { get; init; } = 4;
    public bool AutomaticThreshold { get; init; } = true;
    public byte Threshold { get; init; } = 128;
    public ForegroundPolarity Polarity { get; init; } = ForegroundPolarity.Auto;
    public double MinimumSize { get; init; } = 12;
    public double MaximumSize { get; init; } = 256;
    public double ApproximationTolerance { get; init; } = .02;
    public double MinimumScore { get; init; } = .8;
    public PrimitiveShapeKind Shapes { get; init; } = PrimitiveShapeKind.All;
    /// <summary>0은 전체 검출. 양수는 사용자가 명시한 결과 상한이며 초과하면 불완전으로 보고한다.</summary>
    public int MaximumResults
    {
        get; init;
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Polarity) || Shapes == PrimitiveShapeKind.None || (Shapes & ~PrimitiveShapeKind.All) != 0 ||
            !double.IsFinite(MinimumSize) || !double.IsFinite(MaximumSize) || MinimumSize < 6 ||
            MaximumSize < MinimumSize || MaximumSize > 512 || !double.IsFinite(ApproximationTolerance) ||
            ApproximationTolerance is < .005 or > .04 || !double.IsFinite(MinimumScore) || MinimumScore is < 0 or > 1 ||
            MaximumResults < 0 || MaximumParallelism is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(PrimitiveDetectionOptions),
                "도형 설정: 크기 6~512px, 근사 허용비율 .005~.04, 점수 0~1, 결과 0 이상(0 전체), 도형 한 개 이상을 지정하세요.");
        }
    }
}
