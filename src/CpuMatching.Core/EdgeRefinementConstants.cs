namespace CpuMatching.Core;

/// <summary>
/// 에지 기반 서브픽셀 정밀 자세 보정(EdgePoseRefiner) 및 방향성 점수화(DirectionalPoseScore) 상수 모음.
/// </summary>
public static class EdgeRefinementConstants
{
    // ==========================================
    // 각도 및 수학 단위 변환
    // ==========================================

    /// <summary>원 1회전 주기 각도 (360.0°).</summary>
    public const double FullCircleDegrees = 360.0;

    /// <summary>도(degree)를 라디안(radian)으로 변환하는 계수 (π / 180.0).</summary>
    public const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>라디안(radian)을 도(degree)로 변환하는 계수 (180.0 / π).</summary>
    public const double RadiansToDegrees = 180.0 / Math.PI;

    /// <summary>2π (한 바퀴 라디안).</summary>
    public const double TwoPi = 2.0 * Math.PI;

    // ==========================================
    // 가우스-뉴턴 정밀 반복 파라미터 (Iteration Limits & Steps)
    // ==========================================

    /// <summary>가우스-뉴턴 비선형 최소제곱 수렴 최대 반복 횟수 (8회).</summary>
    public const int MaxRefinementIterations = 8;

    /// <summary>해를 풀기 위한 유효 에지 최소 대응 점 개수 (8개 미만 시 조기 중단).</summary>
    public const int MinEdgeCorrespondenceCount = 8;

    /// <summary>단일 반복 당 허용되는 최대 회전 각도 갱신 보폭 (±2.0°).</summary>
    public const double MaxPerIterationAngleStepDegrees = 2.0;

    /// <summary>단일 반복 당 허용되는 최대 위치 갱신 보폭 (±1.0px).</summary>
    public const double MaxPerIterationTranslationStepPixels = 1.0;

    /// <summary>시드(Seed) 후보 위치 대비 국소 보정 최대 허용 X/Y 이탈량 (3.0px 초과 시 발산 간주).</summary>
    public const double MaxAllowedTranslationDriftPixels = 3.0;

    /// <summary>시드(Seed) 후보 각도 대비 국소 보정 최대 허용 각도 이탈량 (6.0° 초과 시 발산 간주).</summary>
    public const double MaxAllowedAngleDriftDegrees = 6.0;

    /// <summary>수렴 종료 판정 델타 임계치 (|Δx| + |Δy| + |Δθ| &lt; 0.005 시 조기 수렴 완료).</summary>
    public const double ConvergenceTolerance = 0.005;

    /// <summary>가중 최소제곱 로버스트 Huber 가중치 분모 분할 0 방지 엡실론 (1e-6).</summary>
    public const double RobustWeightEpsilon = 1e-6;

    /// <summary>최대 탐색 반경 (주변 에지 탐색 윈도우 크기 3px).</summary>
    public const int NearbyEdgeSearchRadiusPixels = 3;

    /// <summary>동일 방향 판정 허용 각도 오차 한계 (π / 6 = 30° 이내인 에지만 일치로 수용).</summary>
    public const double MaxAngleDifferenceRadians = Math.PI / 6.0;
}
