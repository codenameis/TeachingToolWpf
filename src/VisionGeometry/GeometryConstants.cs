namespace VisionGeometry;

/// <summary>
/// 기하 계산 및 각도 변환에 사용되는 표준 기하 수학 상수.
/// </summary>
public static class GeometryConstants
{
    /// <summary>원 한 바퀴 각도 (360.0도).</summary>
    public const double FullCircleDegrees = 360.0;

    /// <summary>평각 (180.0도).</summary>
    public const double HalfCircleDegrees = 180.0;

    /// <summary>직각 (90.0도).</summary>
    public const double RightAngleDegrees = 90.0;

    /// <summary>도(degree)를 라디안(radian)으로 변환하는 계수 (π / 180.0).</summary>
    public const double DegreesToRadians = Math.PI / HalfCircleDegrees;

    /// <summary>라디안(radian)을 도(degree)로 변환하는 계수 (180.0 / π).</summary>
    public const double RadiansToDegrees = HalfCircleDegrees / Math.PI;

    /// <summary>회전 주기 판정 시 부동소수점 허용치.</summary>
    public const double AngleEpsilonDegrees = 1e-9;
}
