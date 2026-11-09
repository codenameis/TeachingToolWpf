// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Domain/VirtualCornerResult.cs
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

/// <summary>
/// 두 직선의 연립 교차로 산출된 서브픽셀 가상 코너(Virtual Corner) 검출 및 정합 결과.
/// </summary>
public readonly record struct VirtualCornerResult(
    float CornerX,
    float CornerY,
    float OrientationRad,
    float IncludedAngleDeg,
    FittedLine2D Line1,
    FittedLine2D Line2,
    float InlierRatio,
    bool IsValid)
{
    /// <summary>
    /// 검출 실패 시 반환되는 무효 결과.
    /// </summary>
    public static VirtualCornerResult Invalid => new(
        CornerX: float.NaN,
        CornerY: float.NaN,
        OrientationRad: 0f,
        IncludedAngleDeg: 0f,
        Line1: FittedLine2D.Invalid,
        Line2: FittedLine2D.Invalid,
        InlierRatio: 0f,
        IsValid: false);

    /// <summary>
    /// 검출된 가상 코너의 2D 좌표 벡터.
    /// </summary>
    public Vector2 CornerPoint => new(CornerX, CornerY);
}
