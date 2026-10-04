using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

[Flags]
public enum PrimitiveShapeKind
{
    None = 0,
    Triangle = 1,
    Rectangle = 2,
    Circle = 4,
    Cross = 8,
    All = Triangle | Rectangle | Circle | Cross
}

public enum ForegroundPolarity
{
    Auto,
    Bright,
    Dark
}

public sealed record PrimitiveDetectionOptions
{
    public bool AutomaticThreshold { get; init; } = true;
    public byte Threshold { get; init; } = 128;
    public ForegroundPolarity Polarity { get; init; } = ForegroundPolarity.Auto;
    public double MinimumSize { get; init; } = 12;
    public double MaximumSize { get; init; } = 256;
    public double ApproximationTolerance { get; init; } = .02;
    public double MinimumScore { get; init; } = .8;
    public PrimitiveShapeKind Shapes { get; init; } = PrimitiveShapeKind.All;
    public int MaximumResults { get; init; } = 256;

    public void Validate()
    {
        if (!Enum.IsDefined(Polarity) || Shapes == PrimitiveShapeKind.None || (Shapes & ~PrimitiveShapeKind.All) != 0 ||
            !double.IsFinite(MinimumSize) || !double.IsFinite(MaximumSize) || MinimumSize < 6 ||
            MaximumSize < MinimumSize || MaximumSize > 512 || !double.IsFinite(ApproximationTolerance) ||
            ApproximationTolerance is < .005 or > .04 || !double.IsFinite(MinimumScore) || MinimumScore is < 0 or > 1 ||
            MaximumResults is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(PrimitiveDetectionOptions),
                "도형 설정: 크기 6~512px, 근사 허용비율 .005~.04, 점수 0~1, 결과 1~1000, 도형 한 개 이상을 지정하세요.");
        }
    }
}

/// <summary>Contour는 원본 이진 윤곽의 닫힌 꼭짓점 열이며 마지막 점은 첫 점과 암묵적으로 연결한다.</summary>
public sealed record PrimitiveShapeDetection(PrimitiveShapeKind Kind, ImagePoint Center, double AngleRadians,
    double Score, double AreaPixels, ImmutableArray<ImagePoint> Contour);

public sealed record PrimitiveDetectionResult(ImmutableArray<PrimitiveShapeDetection> Shapes,
    ImmutableArray<ImagePoint> ClippedCenters, bool LimitExceeded);
