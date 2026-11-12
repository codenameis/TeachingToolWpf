using System.Runtime.CompilerServices;

namespace VisionGeometry;

/// <summary>
/// ROI 및 기하 연산에 공통으로 사용하는 부동소수점 허용 오차 상수와 좌표 비례 허용치 계산.
/// </summary>
public static class GeometryTolerance
{
    // [Math Why]: 픽셀/서브픽셀 좌표계에서 동일 꼭짓점·최소 선분 길이를 구별하는 기본 거리 허용치(10^-6 px).
    public const double Base = 1e-6;

    // [Math Why]: 외적·행렬식·에너지 제곱근 비교 등 2차항 부동소수점 반올림 잡음 하한(10^-12).
    public const double Squared = 1e-12;

    // [Math Why]: 공선(collinear) 선분 간 접촉 판정 거리 허용치(10^-9 px).
    public const double CollinearDistance = 1e-9;

    // [Math Why]: 퇴화 폴리곤을 걸러내는 최소 부호 면적 하한(10^-8 px^2).
    public const double MinimumSignedArea = 1e-8;

    /// <summary>중심 좌표 크기에 비례해 부동소수점 유효자릿수 손실을 보정한 허용 오차를 반환한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Scaled(PointD point) =>
        Math.Max(Base, Math.Max(Math.Abs(point.X), Math.Abs(point.Y)) * Squared);
}
