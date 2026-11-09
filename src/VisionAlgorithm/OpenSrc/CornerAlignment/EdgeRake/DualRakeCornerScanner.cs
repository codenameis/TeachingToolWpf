// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/EdgeRake/DualRakeCornerScanner.cs
using System;
using System.Collections.Generic;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;

/// <summary>
/// 코너 꼭짓점의 모따기(Chamfer) 및 코너 라운딩(Rounding) 영역(<see cref="RakeSearchRegion.ApexIgnoreMargin"/>)을
/// 자동 회피하며 두 변(Leg 1, Leg 2)을 따라 서브픽셀 에지 점군을 추출하는 Dual-Rake 스캐너.
/// </summary>
public static class DualRakeCornerScanner
{
    private const int MinCaliperStations = 3;

    /// <summary>
    /// 단일 <see cref="RakeSearchRegion"/>을 따라 <see cref="RakeSearchRegion.ApexIgnoreMargin"/> 이후 구간에
    /// 등간격 1D 캘리퍼를 배치하고 서브픽셀 에지 점들을 추출한다.
    /// </summary>
    public static int ScanSingleRake(
        ReadOnlyImageView2D<byte> image,
        RakeSearchRegion region,
        CornerDetectionOptions options,
        Span<SubpixelEdgePoint> destination)
    {
        int caliperCount = Math.Max(MinCaliperStations, region.CaliperCount);
        Vector2 legDir = region.GetUnitLegDirection(out float totalLegLength);
        Vector2 scanDir = region.GetUnitScanDirection();

        float startOffset = Math.Clamp(region.ApexIgnoreMargin, 0.0f, totalLegLength * 0.6f);
        float usableLength = MathF.Max(1.0f, totalLegLength - startOffset);

        int detectedCount = 0;
        int maxOutput = Math.Min(caliperCount, destination.Length);

        for (int i = 0; i < caliperCount && detectedCount < maxOutput; i++)
        {
            float t = caliperCount == 1 ? 0.5f : (float)i / (caliperCount - 1);
            float distanceAlongLeg = startOffset + (t * usableLength);
            Vector2 stationCenter = region.StartPoint + (legDir * distanceAlongLeg);

            if (Simd1DProjectionCaliper.TryMeasureEdge(
                image,
                stationCenter,
                scanDir,
                region.SearchLength,
                region.ProjectionWidth,
                region.Polarity,
                options.GaussianSigma,
                options.MinimumContrast,
                out SubpixelEdgePoint hit))
            {
                destination[detectedCount++] = hit;
            }
        }

        return detectedCount;
    }

    /// <summary>
    /// 가로/세로 두 변(<paramref name="leg1Region"/>, <paramref name="leg2Region"/>)에서 각각 모따기 회피형
    /// 다중 캘리퍼 스캔을 수행하여 두 에지 점군 리스트를 반환한다.
    /// </summary>
    public static (List<SubpixelEdgePoint> Leg1Points, List<SubpixelEdgePoint> Leg2Points) ScanDualRake(
        ReadOnlyImageView2D<byte> image,
        RakeSearchRegion leg1Region,
        RakeSearchRegion leg2Region,
        CornerDetectionOptions options)
    {
        int count1 = Math.Max(MinCaliperStations, leg1Region.CaliperCount);
        int count2 = Math.Max(MinCaliperStations, leg2Region.CaliperCount);

        Span<SubpixelEdgePoint> buf1 = count1 <= 128 ? stackalloc SubpixelEdgePoint[count1] : new SubpixelEdgePoint[count1];
        Span<SubpixelEdgePoint> buf2 = count2 <= 128 ? stackalloc SubpixelEdgePoint[count2] : new SubpixelEdgePoint[count2];

        int n1 = ScanSingleRake(image, leg1Region, options, buf1);
        int n2 = ScanSingleRake(image, leg2Region, options, buf2);

        var list1 = new List<SubpixelEdgePoint>(n1);
        for (int i = 0; i < n1; i++)
        {
            list1.Add(buf1[i]);
        }

        var list2 = new List<SubpixelEdgePoint>(n2);
        for (int i = 0; i < n2; i++)
        {
            list2.Add(buf2[i]);
        }

        return (list1, list2);
    }
}
