using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

/// <summary>Contour는 원본 이진 윤곽의 닫힌 꼭짓점 열이며 마지막 점은 첫 점과 암묵적으로 연결한다.</summary>
public sealed record PrimitiveShapeDetection(PrimitiveShapeKind Kind, ImagePoint Center, double AngleRadians,
    double Score, double AreaPixels, ImmutableArray<ImagePoint> Contour)
{
    /// <summary>빈 구멍의 닫힌 윤곽. 별도 검출로 세지 않으며 AreaPixels에서 제외한다.</summary>
    public ImmutableArray<ImmutableArray<ImagePoint>> InnerContours { get; init; } = [];
    /// <summary>첫 Contour와 떨어진 추가 전경 조각. 구멍이 아니며 면적에 포함한다.</summary>
    public ImmutableArray<ImmutableArray<ImagePoint>> AdditionalContours { get; init; } = [];
}
