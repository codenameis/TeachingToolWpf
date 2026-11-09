// File: src/VisionAlgorithm/OpenSrc/Curvilinear/BeadContinuityInspector.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;

namespace VisionAlgorithm.OpenSrc.Curvilinear;

/// <summary>
/// Represents a single cross-sectional station measurement along a curvilinear path (sealant bead, wire bond, or weld seam).
/// </summary>
public readonly record struct BeadCrossSectionSample(
    int StationIndex,
    Vector2 NominalCenterWorld,
    bool IsPresent,
    Vector2 MeasuredCenterWorld,
    float MeasuredWidthPixels,
    float PeakContrast);

/// <summary>
/// Summary report of a curvilinear continuity and width profile inspection.
/// </summary>
public readonly record struct BeadContinuityResult(
    bool IsContinuousAndWithinSpec,
    int TotalStations,
    int ValidStations,
    int GapStationCount,
    int NeckingStationCount,
    int BulgeStationCount,
    float MinWidthPixels,
    float MaxWidthPixels,
    float MeanWidthPixels,
    IReadOnlyList<BeadCrossSectionSample> Samples);

/// <summary>
/// Inspects continuity, gaps (open circuits / broken glue beads), necking (thinning), and bulging along an arbitrary
/// 2D path by combining Steger centerline localization and orthogonal 1D subpixel edge-pair calipers.
/// </summary>
public static class BeadContinuityInspector
{
    // [Perf Why]: 5 orthogonal projection strips average local bead texture while preserving curved cross-section edges.
    private const int DefaultCrossSectionStripCount = 5;

    // [Math Why]: Minimum cross-sectional caliper scan length (16 px) ensures sufficient background padding on both sides of thin beads.
    private const float MinimumScanLengthPixels = 16f;

    // [Math Why]: Scanning 3x the nominal bead width guarantees capturing both left and right edges even under lateral bead wander.
    private const float ScanLengthNominalWidthMultiplier = 3.0f;

    // [Math Why]: Minimum chord length floor prevents division by zero when consecutive path waypoints coincide.
    private const float MinTangentLengthEpsilon = 1e-4f;

    // [Math Why]: Orthogonal projection width (5 px) along the path tangent suppresses sensor noise without smearing high-curvature bends.
    private const float DefaultCrossSectionProjectionWidthPixels = 5f;

    // [Math Why]: If the measured edge-pair midpoint deviates from the nominal path center by more than 1.2x nominal width,
    // the caliper locked onto background clutter rather than the bead at this station.
    private const float MaxCenterDeviationWidthRatio = 1.2f;

    /// <summary>
    /// Evaluates cross-sectional presence and subpixel width at every station along <paramref name="nominalPathWorld"/>
    /// on <paramref name="imageView"/>.
    /// </summary>
    public static BeadContinuityResult InspectPath(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<Vector2> nominalPathWorld,
        RidgePolarity polarity,
        float nominalWidthPixels,
        float minAllowedWidthPixels,
        float maxAllowedWidthPixels,
        float minimumContrast,
        InspectionWorkspace workspace)
    {
        imageView.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);
        if (nominalPathWorld.Length < 2)
        {
            throw new ArgumentException("At least 2 path waypoints are required to define tangent directions.", nameof(nominalPathWorld));
        }

        int stationCount = nominalPathWorld.Length;
        List<BeadCrossSectionSample> samples = new(stationCount);

        CaliperEdgePolarity firstPol = polarity == RidgePolarity.BrightLine
            ? CaliperEdgePolarity.DarkToLight
            : CaliperEdgePolarity.LightToDark;
        CaliperEdgePolarity secondPol = polarity == RidgePolarity.BrightLine
            ? CaliperEdgePolarity.LightToDark
            : CaliperEdgePolarity.DarkToLight;

        CaliperMeasurementSpec caliperSpec = new(
            gaussianSigma: 1.0f,
            minimumContrast: minimumContrast,
            projectionStripCount: DefaultCrossSectionStripCount);

        float scanLength = MathF.Max(MinimumScanLengthPixels, nominalWidthPixels * ScanLengthNominalWidthMultiplier);

        int validCount = 0;
        int gapCount = 0;
        int neckingCount = 0;
        int bulgeCount = 0;
        float minWidth = float.MaxValue;
        float maxWidth = 0f;
        double sumWidth = 0.0;

        for (int i = 0; i < stationCount; i++)
        {
            Vector2 prev = nominalPathWorld[Math.Max(0, i - 1)];
            Vector2 next = nominalPathWorld[Math.Min(stationCount - 1, i + 1)];
            Vector2 tangent = next - prev;
            float tangentLen = tangent.Length();
            if (tangentLen < MinTangentLengthEpsilon)
            {
                tangent = Vector2.UnitX;
            }
            else
            {
                tangent /= tangentLen;
            }

            // Orthogonal normal direction (-tangent.Y, tangent.X) defines the cross-sectional caliper scan axis
            float normalAngle = MathF.Atan2(tangent.X, -tangent.Y);
            Vector2 stationCenter = nominalPathWorld[i];
            FixturePose2D crossSectionPose = new(stationCenter.X, stationCenter.Y, normalAngle, 1f);

            CaliperPairResult pair = SubpixelCaliper1D.MeasureEdgePairWidth(
                imageView,
                crossSectionPose,
                scanLength,
                projectionWidthPixels: DefaultCrossSectionProjectionWidthPixels,
                firstPolarity: firstPol,
                secondPolarity: secondPol,
                spec: caliperSpec,
                workspace: workspace);

            if (!pair.IsFound || Vector2.Distance(pair.MidpointWorld, stationCenter) > nominalWidthPixels * MaxCenterDeviationWidthRatio)
            {
                gapCount++;
                samples.Add(new BeadCrossSectionSample(
                    StationIndex: i,
                    NominalCenterWorld: stationCenter,
                    IsPresent: false,
                    MeasuredCenterWorld: stationCenter,
                    MeasuredWidthPixels: 0f,
                    PeakContrast: 0f));
                continue;
            }

            validCount++;
            float w = pair.SubpixelWidth;
            float contrast = 0.5f * (pair.FirstEdge.GradientAmplitude + pair.SecondEdge.GradientAmplitude);

            if (w < minWidth)
            {
                minWidth = w;
            }

            if (w > maxWidth)
            {
                maxWidth = w;
            }

            sumWidth += w;

            if (w < minAllowedWidthPixels)
            {
                neckingCount++;
            }
            else if (w > maxAllowedWidthPixels)
            {
                bulgeCount++;
            }

            samples.Add(new BeadCrossSectionSample(
                StationIndex: i,
                NominalCenterWorld: stationCenter,
                IsPresent: true,
                MeasuredCenterWorld: pair.MidpointWorld,
                MeasuredWidthPixels: w,
                PeakContrast: contrast));
        }

        if (validCount == 0)
        {
            minWidth = 0f;
        }

        float meanWidth = validCount > 0 ? (float)(sumWidth / validCount) : 0f;
        bool isPass = gapCount == 0 && neckingCount == 0 && bulgeCount == 0;

        return new BeadContinuityResult(
            IsContinuousAndWithinSpec: isPass,
            TotalStations: stationCount,
            ValidStations: validCount,
            GapStationCount: gapCount,
            NeckingStationCount: neckingCount,
            BulgeStationCount: bulgeCount,
            MinWidthPixels: minWidth,
            MaxWidthPixels: maxWidth,
            MeanWidthPixels: meanWidth,
            Samples: samples);
    }

    /// <summary>
    /// Evaluates cross-sectional presence and subpixel width at every station along <paramref name="nominalPathWorld"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BeadContinuityResult InspectPath(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        ReadOnlySpan<Vector2> nominalPathWorld,
        RidgePolarity polarity,
        float nominalWidthPixels,
        float minAllowedWidthPixels,
        float maxAllowedWidthPixels,
        float minimumContrast,
        InspectionWorkspace workspace)
        => InspectPath(
            new ReadOnlyImageView2D<byte>(image, width, height, stride),
            nominalPathWorld,
            polarity,
            nominalWidthPixels,
            minAllowedWidthPixels,
            maxAllowedWidthPixels,
            minimumContrast,
            workspace);
}
