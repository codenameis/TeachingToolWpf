// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/EdgeRake/SubpixelEdgePoint.cs
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;

/// <summary>
/// 1D 프로젝션 캘리퍼에서 추출된 서브픽셀 에지 점 좌표 및 응답 강도 정보.
/// </summary>
public readonly record struct SubpixelEdgePoint(
    float X,
    float Y,
    float Magnitude,
    float Score,
    bool IsInlier)
{
    /// <summary>
    /// 서브픽셀 에지 점의 2D 좌표 벡터.
    /// </summary>
    public Vector2 Position => new(X, Y);
}
