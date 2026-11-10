// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/LineAndCorner/VirtualCornerAligner.cs
using System;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.LineAndCorner;

/// <summary>
/// 두 직선의 Cramer 공식 연립 교차점 산출, 내각 직각도 검증, 그리고
/// 단일 코너(1-Corner) · 2-코너 기저선(2-Corner) · 대각/4-코너(Diagonal/4-Corner Kabsch SVD) 통합 정합기.
/// </summary>
public static class VirtualCornerAligner
{
    private const float MinDeterminantThreshold = 1e-5f;
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float NominalOrthogonalAngleDeg = 90.0f;
    private const int StackAllocPointLimit = 128;

    /// <summary>
    /// 영상과 두 변의 <see cref="RakeSearchRegion"/>으로부터 Dual-Rake 서브픽셀 에지 추출,
    /// RANSAC + Tukey Biweight 직선 피팅, Cramer 교차점 계산 및 직각도 검증을 일괄 수행한다.
    /// </summary>
    public static VirtualCornerResult DetectVirtualCorner(
        ReadOnlyImageView2D<byte> image,
        RakeSearchRegion leg1Region,
        RakeSearchRegion leg2Region,
        CornerDetectionOptions options)
        => DetectVirtualCornerWithPoints(image, leg1Region, leg2Region, options, out _, out _);

    /// <summary>
    /// 영상과 두 변의 <see cref="RakeSearchRegion"/>으로부터 독립된 Dual-Rake 서브픽셀 에지 추출,
    /// 2D 그래디언트 방향 게이트 기각, 독립 RANSAC + Tukey Biweight 직선 피팅, Cramer 교차점 계산을 수행하고
    /// 채택된 인라이어 점군과 기각된 이상치(모따기·칩핑·버) 점군을 함께 반환한다.
    /// </summary>
    public static VirtualCornerResult DetectVirtualCornerWithPoints(
        ReadOnlyImageView2D<byte> image,
        RakeSearchRegion leg1Region,
        RakeSearchRegion leg2Region,
        CornerDetectionOptions options,
        out SubpixelEdgePoint[] inlierPoints,
        out SubpixelEdgePoint[] outlierPoints)
    {
        inlierPoints = [];
        outlierPoints = [];

        int cap1 = Math.Max(4, leg1Region.CaliperCount);
        int cap2 = Math.Max(4, leg2Region.CaliperCount);

        Span<SubpixelEdgePoint> pts1 = cap1 <= StackAllocPointLimit ? stackalloc SubpixelEdgePoint[cap1] : new SubpixelEdgePoint[cap1];
        Span<SubpixelEdgePoint> pts2 = cap2 <= StackAllocPointLimit ? stackalloc SubpixelEdgePoint[cap2] : new SubpixelEdgePoint[cap2];
        Span<SubpixelEdgePoint> rej1 = cap1 <= StackAllocPointLimit ? stackalloc SubpixelEdgePoint[cap1] : new SubpixelEdgePoint[cap1];
        Span<SubpixelEdgePoint> rej2 = cap2 <= StackAllocPointLimit ? stackalloc SubpixelEdgePoint[cap2] : new SubpixelEdgePoint[cap2];

        int count1 = DualRakeCornerScanner.ScanSingleRake(image, leg1Region, options, pts1, rej1, out int rejCount1);
        int count2 = DualRakeCornerScanner.ScanSingleRake(image, leg2Region, options, pts2, rej2, out int rejCount2);

        if (count1 < 3 || count2 < 3)
        {
            return VirtualCornerResult.Invalid;
        }

        Vector2 dir1 = leg1Region.GetUnitLegDirection(out _);
        Vector2 dir2 = leg2Region.GetUnitLegDirection(out _);

        Span<SubpixelEdgePoint> slice1 = pts1.Slice(0, count1);
        Span<SubpixelEdgePoint> slice2 = pts2.Slice(0, count2);

        // 두 변을 완전히 독립된 직교 거리 회귀(RANSAC + Tukey Biweight IRLS)로 각각 피팅한다.
        FittedLine2D line1 = HessianNormalLineFitter.FitLine(slice1, options, dir1);
        FittedLine2D line2 = HessianNormalLineFitter.FitLine(slice2, options, dir2);

        int inlierTotal = 0;
        for (int i = 0; i < count1; i++)
        {
            if (slice1[i].IsInlier)
            {
                inlierTotal++;
            }
        }
        for (int i = 0; i < count2; i++)
        {
            if (slice2[i].IsInlier)
            {
                inlierTotal++;
            }
        }

        int outlierTotal = (count1 + count2 - inlierTotal) + rejCount1 + rejCount2;
        inlierPoints = new SubpixelEdgePoint[inlierTotal];
        outlierPoints = new SubpixelEdgePoint[outlierTotal];

        int inIdx = 0;
        int outIdx = 0;
        for (int i = 0; i < count1; i++)
        {
            if (slice1[i].IsInlier)
            {
                inlierPoints[inIdx++] = slice1[i];
            }
            else
            {
                outlierPoints[outIdx++] = slice1[i];
            }
        }
        for (int i = 0; i < count2; i++)
        {
            if (slice2[i].IsInlier)
            {
                inlierPoints[inIdx++] = slice2[i];
            }
            else
            {
                outlierPoints[outIdx++] = slice2[i];
            }
        }
        for (int i = 0; i < rejCount1; i++)
        {
            outlierPoints[outIdx++] = rej1[i];
        }
        for (int i = 0; i < rejCount2; i++)
        {
            outlierPoints[outIdx++] = rej2[i];
        }

        int totalCalipers = Math.Max(1, leg1Region.CaliperCount + leg2Region.CaliperCount);
        return IntersectFittedLines(line1, line2, totalCalipers, options);
    }

    /// <summary>
    /// 피팅된 두 헤세 법선 직선의 Cramer 공식 교차점을 계산하고 내각 직각도 공차를 검증한다.
    /// </summary>
    public static VirtualCornerResult IntersectFittedLines(
        FittedLine2D line1,
        FittedLine2D line2,
        int totalExpectedCalipers,
        CornerDetectionOptions options)
    {
        if (!line1.IsValid || !line2.IsValid)
        {
            return VirtualCornerResult.Invalid;
        }

        double n1x = line1.Nx;
        double n1y = line1.Ny;
        double rho1 = line1.Rho;

        double n2x = line2.Nx;
        double n2y = line2.Ny;
        double rho2 = line2.Rho;

        double det = (n1x * n2y) - (n1y * n2x);
        if (Math.Abs(det) < MinDeterminantThreshold)
        {
            return VirtualCornerResult.Invalid;
        }

        double invDet = 1.0 / det;
        double xc = ((rho1 * n2y) - (rho2 * n1y)) * invDet;
        double yc = ((n1x * rho2) - (n2x * rho1)) * invDet;

        // 독립 피팅된 두 직선의 실제 법선 벡터(n1, n2)로부터 내각을 산출한다.
        double dotNormals = (n1x * n2x) + (n1y * n2y);
        float includedAngleDeg = (float)(Math.Atan2(Math.Abs(det), Math.Abs(dotNormals)) * RadiansToDegrees);
        float angleDeviationDeg = MathF.Abs(NominalOrthogonalAngleDeg - includedAngleDeg);

        float inlierRatio = (float)(line1.InlierCount + line2.InlierCount) / Math.Max(1, totalExpectedCalipers);
        bool isValid = angleDeviationDeg <= options.OrthogonalityToleranceDeg
            && inlierRatio >= options.MinimumInlierRatio;

        return new VirtualCornerResult(
            CornerX: (float)xc,
            CornerY: (float)yc,
            OrientationRad: line1.AngleRad,
            IncludedAngleDeg: includedAngleDeg,
            Line1: line1,
            Line2: line2,
            InlierRatio: inlierRatio,
            IsValid: isValid);
    }

    /// <summary>
    /// 1-Corner 정합 모드: 단일 가상 코너의 교차점 좌표와 기준 변(Leg 1)의 방향각을 반환한다.
    /// </summary>
    public static (Vector2 Position, float AngleRad, bool IsValid) AlignSingleCorner(VirtualCornerResult corner)
        => (corner.CornerPoint, corner.OrientationRad, corner.IsValid);

    /// <summary>
    /// 2-Corner 정합 모드: 좌·우 두 코너 교차점의 중점과 두 코너를 잇는 장거리 기저선(Baseline) 각도를 산출한다.
    /// </summary>
    public static (Vector2 Midpoint, float BaselineAngleRad, bool IsValid) AlignTwoCorners(
        VirtualCornerResult firstCorner,
        VirtualCornerResult secondCorner)
    {
        if (!firstCorner.IsValid || !secondCorner.IsValid)
        {
            return (default, 0.0f, false);
        }

        Vector2 p1 = firstCorner.CornerPoint;
        Vector2 p2 = secondCorner.CornerPoint;
        Vector2 midpoint = 0.5f * (p1 + p2);
        float angleRad = MathF.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        return (midpoint, angleRad, true);
    }

    /// <summary>
    /// 대각(2점) 또는 4-코너 정합 모드: 코너 점군의 무게중심으로 제조 외곽 재단 공차를 양분하여 상쇄하고,
    /// 2D Kabsch(Procrustes SVD) 닫힌 해로 기준 코너 점군 대비 최적 강체 회전각을 산출한다.
    /// </summary>
    public static (Vector2 Centroid, float OptimalRotationRad, Vector2 TranslationDelta, bool IsValid) AlignDiagonalCorners(
        ReadOnlySpan<Vector2> referenceCorners,
        ReadOnlySpan<Vector2> measuredCorners)
    {
        int count = referenceCorners.Length;
        if (count < 2 || measuredCorners.Length != count)
        {
            return (default, 0.0f, default, false);
        }

        double refSumX = 0.0;
        double refSumY = 0.0;
        double measSumX = 0.0;
        double measSumY = 0.0;

        for (int i = 0; i < count; i++)
        {
            refSumX += referenceCorners[i].X;
            refSumY += referenceCorners[i].Y;
            measSumX += measuredCorners[i].X;
            measSumY += measuredCorners[i].Y;
        }

        double invCount = 1.0 / count;
        double refCx = refSumX * invCount;
        double refCy = refSumY * invCount;
        double measCx = measSumX * invCount;
        double measCy = measSumY * invCount;

        double crossSum = 0.0;
        double dotSum = 0.0;

        for (int i = 0; i < count; i++)
        {
            double rx = referenceCorners[i].X - refCx;
            double ry = referenceCorners[i].Y - refCy;
            double mx = measuredCorners[i].X - measCx;
            double my = measuredCorners[i].Y - measCy;

            crossSum += (rx * my) - (ry * mx);
            dotSum += (rx * mx) + (ry * my);
        }

        float deltaThetaRad = (float)Math.Atan2(crossSum, dotSum);
        Vector2 measuredCentroid = new((float)measCx, (float)measCy);
        Vector2 translationDelta = new((float)(measCx - refCx), (float)(measCy - refCy));

        return (measuredCentroid, deltaThetaRad, translationDelta, true);
    }
}
