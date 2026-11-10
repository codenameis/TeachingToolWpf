using System;
using System.Numerics;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Configuration specification for <see cref="CornerAlignmentToolNode"/>.
/// Defines the two orthogonal or near-orthogonal Dual-Rake search legs and robust line fitting parameters.
/// </summary>
public readonly record struct CornerAlignmentToolSpec
{
    private const int MinStationCount = 4;
    private const int MaxStationCount = 64;
    private const float MinSearchLength = 4.0f;
    private const float MinProjectionWidth = 3.0f;

    public Vector2 ExpectedApexWorld { get; }
    public Vector2 Leg1EndWorld { get; }
    public Vector2 Leg2EndWorld { get; }
    public float SearchLengthPixels { get; }
    public float ProjectionWidthPixels { get; }
    public int CaliperCountPerLeg { get; }
    public float ApexIgnoreMarginPixels { get; }
    public CaliperPolarity Leg1Polarity { get; }
    public CaliperPolarity Leg2Polarity { get; }
    public CornerDetectionOptions DetectionOptions { get; }
    public bool IsInitialized { get; }

    private CornerAlignmentToolSpec(
        Vector2 expectedApexWorld,
        Vector2 leg1EndWorld,
        Vector2 leg2EndWorld,
        float searchLengthPixels,
        float projectionWidthPixels,
        int caliperCountPerLeg,
        float apexIgnoreMarginPixels,
        CaliperPolarity leg1Polarity,
        CaliperPolarity leg2Polarity,
        CornerDetectionOptions detectionOptions)
    {
        ExpectedApexWorld = expectedApexWorld;
        Leg1EndWorld = leg1EndWorld;
        Leg2EndWorld = leg2EndWorld;
        SearchLengthPixels = searchLengthPixels;
        ProjectionWidthPixels = projectionWidthPixels;
        CaliperCountPerLeg = caliperCountPerLeg;
        ApexIgnoreMarginPixels = apexIgnoreMarginPixels;
        Leg1Polarity = leg1Polarity;
        Leg2Polarity = leg2Polarity;
        DetectionOptions = detectionOptions;
        IsInitialized = true;
    }

    public static CornerAlignmentToolSpec Create(
        Vector2 expectedApexWorld,
        Vector2 leg1EndWorld,
        Vector2 leg2EndWorld,
        float searchLengthPixels = 28.0f,
        float projectionWidthPixels = 9.0f,
        int caliperCountPerLeg = 12,
        float apexIgnoreMarginPixels = 10.0f,
        CaliperPolarity leg1Polarity = CaliperPolarity.Any,
        CaliperPolarity leg2Polarity = CaliperPolarity.Any,
        CornerDetectionOptions? detectionOptions = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(searchLengthPixels, MinSearchLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(projectionWidthPixels, MinProjectionWidth);
        int clampedStations = Math.Clamp(caliperCountPerLeg, MinStationCount, MaxStationCount);
        float clampedIgnore = MathF.Max(0.0f, apexIgnoreMarginPixels);

        return new CornerAlignmentToolSpec(
            expectedApexWorld,
            leg1EndWorld,
            leg2EndWorld,
            searchLengthPixels,
            projectionWidthPixels,
            clampedStations,
            clampedIgnore,
            leg1Polarity,
            leg2Polarity,
            detectionOptions ?? CornerDetectionOptions.Default);
    }

    /// <summary>
    /// Creates a Dual-Rake corner specification from an interactive bounding rectangle ROI,
    /// placing the expected corner near the top-left of the ROI and extending Leg 1 horizontally and Leg 2 vertically.
    /// </summary>
    public static CornerAlignmentToolSpec CreateFromRoiBounds(
        float roiX,
        float roiY,
        float roiWidth,
        float roiHeight,
        float searchLengthPixels = 26.0f,
        int caliperCountPerLeg = 12,
        float apexIgnoreMarginPixels = 8.0f)
    {
        const float CornerInsetFraction = 0.18f;
        const float LegReachFraction = 0.85f;
        const float DefaultProjectionWidth = 9.0f;

        float apexX = roiX + (roiWidth * CornerInsetFraction);
        float apexY = roiY + (roiHeight * CornerInsetFraction);
        Vector2 apex = new(apexX, apexY);
        Vector2 leg1End = new(roiX + (roiWidth * LegReachFraction), apexY);
        Vector2 leg2End = new(apexX, roiY + (roiHeight * LegReachFraction));

        return Create(
            apex,
            leg1End,
            leg2End,
            MathF.Max(MinSearchLength, searchLengthPixels),
            DefaultProjectionWidth,
            caliperCountPerLeg,
            apexIgnoreMarginPixels);
    }
}
