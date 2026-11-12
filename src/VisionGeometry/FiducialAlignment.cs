using System.Diagnostics.CodeAnalysis;

namespace VisionGeometry;

/// <summary>기준/검출 마크 1~4쌍으로 만든 불변 XY/XYθ 보정. 기준 좌표는 항상 원래 티칭 좌표다.</summary>
public sealed class FiducialAlignment
{
    private const double MinimumMarkDistancePixels = 1;

    public FiducialAlignment? Parent { get; }

    /// <summary>상위 보정이 적용된 좌표에 더하는 보정이다.</summary>
    public RigidTransform2D LocalTransform { get; }

    /// <summary>원래 티칭 좌표에 한 번 적용하는 합성 보정이다.</summary>
    public RigidTransform2D Transform { get; }

    public int MarkCount { get; }

    public double RmsErrorPixels { get; }

    public double MaxErrorPixels { get; }

    private FiducialAlignment(FiducialAlignment? parent, RigidTransform2D localTransform, RigidTransform2D transform,
        int markCount, double rmsErrorPixels, double maxErrorPixels)
    {
        Parent = parent;
        LocalTransform = localTransform;
        Transform = transform;
        MarkCount = markCount;
        RmsErrorPixels = rmsErrorPixels;
        MaxErrorPixels = maxErrorPixels;
    }

    /// <summary>
    /// 같은 인덱스는 같은 마크다. 1점은 이동만, 2~4점은 모든 점의 최소제곱 강체 보정을 계산한다.
    /// 각 마크는 서로 1px 이상 떨어져야 한다. 최대 잔차가 허용치를 넘으면 결과 없이 실패한다.
    /// </summary>
    public static bool TryCreate(ReadOnlySpan<PointD> referenceMarks, ReadOnlySpan<PointD> detectedMarks,
        [NotNullWhen(true)] out FiducialAlignment? alignment, out string error,
        double maximumErrorPixels = 1, FiducialAlignment? parent = null)
    {
        alignment = null;
        error = string.Empty;
        if (referenceMarks.Length is < 1 or > 4 || referenceMarks.Length != detectedMarks.Length)
        {
            error = "기준 마크와 검출 마크를 같은 순서로 1~4쌍 입력하세요.";
            return false;
        }
        if (!double.IsFinite(maximumErrorPixels) || maximumErrorPixels < 0)
        {
            error = "허용 오차는 유한한 0 이상의 픽셀 값이어야 합니다.";
            return false;
        }
        if (!ValidateMarks(referenceMarks) || !ValidateMarks(detectedMarks))
        {
            error = "마크 좌표는 유한해야 하며, 각 마크는 서로 1px 이상 떨어져야 합니다.";
            return false;
        }

        var referenceCenter = Centroid(referenceMarks);
        var detectedCenter = Centroid(detectedMarks);
        var inherited = parent?.Transform ?? RigidTransform2D.Identity;
        var currentCenter = inherited.Apply(referenceCenter);
        var localTranslation = detectedCenter - currentCenter;
        var translation = detectedCenter - referenceCenter;
        if (!currentCenter.IsFinite || !localTranslation.IsFinite || !translation.IsFinite)
        {
            error = "마크 좌표 범위가 너무 커 보정값을 계산할 수 없습니다.";
            return false;
        }
        if (!TryFindRotation(referenceMarks, detectedMarks, referenceCenter, detectedCenter, inherited, out var angle))
        {
            error = "마크 대응에서 회전을 결정할 수 없습니다. 마크 순서와 좌표를 확인하세요.";
            return false;
        }

        var local = new RigidTransform2D(currentCenter, angle, localTranslation);
        // 기준 중심이 검출 중심으로 가도록 합성한다. 이동량을 단순히 더하면 회전 시 틀어진다.
        var combined = new RigidTransform2D(referenceCenter, Math.IEEERemainder(inherited.AngleDegrees + angle, GeometryConstants.FullCircleDegrees), translation);
        var squaredError = 0d;
        var maxError = 0d;
        for (var i = 0; i < referenceMarks.Length; i++)
        {
            var residual = combined.Apply(referenceMarks[i]) - detectedMarks[i];
            var distance = double.Hypot(residual.X, residual.Y);
            maxError = Math.Max(maxError, distance);
            squaredError += distance * distance;
        }
        if (!double.IsFinite(squaredError) || maxError > maximumErrorPixels)
        {
            error = $"마크 최대 오차 {maxError:F3}px가 허용치 {maximumErrorPixels:F3}px를 넘습니다. 마크 대응과 배율 변화를 확인하세요.";
            return false;
        }

        alignment = new(parent, local, combined, referenceMarks.Length, Math.Sqrt(squaredError / referenceMarks.Length), maxError);
        return true;
    }

    /// <summary>원본 ROI를 변경하지 않고 크기와 형상을 유지한 검사 좌표를 반환한다. 경계를 잘라내지 않는다.</summary>
    public RoiGeometry Apply(RoiGeometry referenceRoi)
    {
        ArgumentNullException.ThrowIfNull(referenceRoi);
        return Transform.IsIdentity ? referenceRoi : referenceRoi.WithFrame(
            Transform.Apply(referenceRoi.Center), referenceRoi.Width, referenceRoi.Height, referenceRoi.Angle + Transform.AngleDegrees);
    }

    private static bool ValidateMarks(ReadOnlySpan<PointD> marks)
    {
        for (var i = 0; i < marks.Length; i++)
        {
            if (!marks[i].IsFinite)
            {
                return false;
            }
            for (var j = 0; j < i; j++)
            {
                var delta = marks[i] - marks[j];
                var distance = double.Hypot(delta.X, delta.Y);
                if (!double.IsFinite(distance) || distance < MinimumMarkDistancePixels)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static PointD Centroid(ReadOnlySpan<PointD> marks)
    {
        var center = new PointD();
        foreach (var mark in marks)
        {
            center += mark / marks.Length;
        }
        return center;
    }

    private static bool TryFindRotation(ReadOnlySpan<PointD> reference, ReadOnlySpan<PointD> detected,
        PointD referenceCenter, PointD detectedCenter, RigidTransform2D inherited, out double angle)
    {
        angle = 0;
        if (reference.Length == 1)
        {
            return true;
        }

        var dot = 0d;
        var cross = 0d;
        var referenceEnergy = 0d;
        var detectedEnergy = 0d;
        for (var i = 0; i < reference.Length; i++)
        {
            var from = inherited.ApplyVector(reference[i] - referenceCenter);
            var to = detected[i] - detectedCenter;
            dot += (from.X * to.X) + (from.Y * to.Y);
            cross += (from.X * to.Y) - (from.Y * to.X);
            referenceEnergy += (from.X * from.X) + (from.Y * from.Y);
            detectedEnergy += (to.X * to.X) + (to.Y * to.Y);
        }
        var correlation = double.Hypot(dot, cross);
        var energy = Math.Sqrt(referenceEnergy) * Math.Sqrt(detectedEnergy);
        if (!double.IsFinite(correlation) || !double.IsFinite(energy) || correlation <= GeometryTolerance.Squared * energy)
        {
            return false;
        }
        angle = Math.Atan2(cross, dot) * GeometryConstants.RadiansToDegrees;
        return true;
    }
}
