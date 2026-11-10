namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Result of multi-feature sub-pixel geometric dimensioning and tolerancing (GD&amp;T):
/// Line-to-Line intersection angle $\Delta\theta$ and perpendicular distance, Circle-to-Circle concentricity $\Delta c$,
/// Bore radius, and Point-to-Line perpendicular distance with min/max tolerance verification.
/// </summary>
public readonly record struct GeometricToleranceResult(
    bool IsMeasured,
    bool IsAllWithinTolerance,
    double LineToLineAngleDeg,
    double LineToLinePerpendicularDistancePx,
    double CircleConcentricityOffsetPx,
    double OuterCircleRadiusPx,
    double InnerCircleRadiusPx,
    double BoreToDatumDistancePx,
    double AngleErrorDeg,
    double DistanceErrorPx,
    double DatumIntersectionX,
    double DatumIntersectionY,
    double OuterCircleCenterX,
    double OuterCircleCenterY,
    double InnerCircleCenterX,
    double InnerCircleCenterY);
