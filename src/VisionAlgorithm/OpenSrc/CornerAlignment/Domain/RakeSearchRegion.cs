// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Domain/RakeSearchRegion.cs
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

/// <summary>
/// 코너의 단일 변(Leg)을 따라 등간격 배치되는 다중 1D 프로젝션 캘리퍼(Rake) 탐색 영역 설정.
/// <see cref="StartPoint"/>는 예상 코너 꼭짓점 부근이며, <see cref="ApexIgnoreMargin"/>만큼 떨어진 지점부터
/// <see cref="EndPoint"/>까지 <see cref="CaliperCount"/>개의 1D 캘리퍼를 배치해 모따기·라운딩 왜곡을 회피한다.
/// </summary>
public readonly record struct RakeSearchRegion(
    Vector2 StartPoint,
    Vector2 EndPoint,
    float SearchLength,
    float ProjectionWidth,
    int CaliperCount,
    float ApexIgnoreMargin,
    CaliperPolarity Polarity = CaliperPolarity.Any,
    Vector2 ExplicitScanDirection = default)
{
    private const float MinDirectionNormSq = 1e-6f;

    /// <summary>
    /// 시작점(꼭짓점 측)에서 끝점 방향으로의 단위 접선 벡터를 반환한다.
    /// </summary>
    public Vector2 GetUnitLegDirection(out float totalLegLength)
    {
        Vector2 delta = EndPoint - StartPoint;
        totalLegLength = delta.Length();
        if (totalLegLength * totalLegLength < MinDirectionNormSq)
        {
            return new Vector2(1.0f, 0.0f);
        }

        return delta / totalLegLength;
    }

    /// <summary>
    /// 각 1D 캘리퍼가 스캔을 수행할 단위 법선 벡터를 반환한다.
    /// <see cref="ExplicitScanDirection"/>이 지정된 경우 이를 정규화하여 사용하고,
    /// 미지정 시 변 진행 방향의 우측 수직 법선 <c>(-ty, tx)</c>을 사용한다.
    /// </summary>
    public Vector2 GetUnitScanDirection()
    {
        if (ExplicitScanDirection.LengthSquared() > MinDirectionNormSq)
        {
            return Vector2.Normalize(ExplicitScanDirection);
        }

        Vector2 tangent = GetUnitLegDirection(out _);
        return new Vector2(-tangent.Y, tangent.X);
    }
}
