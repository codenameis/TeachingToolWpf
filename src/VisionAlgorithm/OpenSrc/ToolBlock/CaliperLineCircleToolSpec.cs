// File: src/VisionAlgorithm/OpenSrc/ToolBlock/CaliperLineCircleToolSpec.cs
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Immutable configuration specification for <see cref="CaliperLineCircleToolNode"/> covering 1D single-edge,
/// edge-pair width, multi-caliper RANSAC+IRLS line fitting, and radial spoke Taubin circle fitting.
/// </summary>
public readonly record struct CaliperLineCircleToolSpec
{
    // [Math Why]: Minimum rake/spoke count (3) guarantees well-posed line or circle estimation.
    private const int MinimumSampleCount = 3;

    // [Math Why]: Default RANSAC inlier threshold (1.5 px) accommodates sub-pixel edge jitter while rejecting outliers.
    private const float DefaultRansacInlierThresholdPixels = 1.5f;

    private readonly bool _isInitialized;

    public CaliperMetroMode Mode { get; }
    public FixturePose2D LocalRoiOffset { get; }
    public float ScanLengthPixels { get; }
    public float ProjectionWidthPixels { get; }
    public CaliperEdgePolarity FirstPolarity { get; }
    public CaliperEdgePolarity SecondPolarity { get; }
    public CaliperMeasurementSpec CaliperSpec { get; }
    public int SampleCount { get; }
    public float NominalRadiusOrSpanPixels { get; }
    public float RansacInlierThresholdPixels { get; }

    public bool IsValid => _isInitialized && LocalRoiOffset.IsValid && CaliperSpec.IsValid && ScanLengthPixels > 0f;

    public CaliperLineCircleToolSpec(
        CaliperMetroMode mode,
        FixturePose2D localRoiOffset,
        float scanLengthPixels,
        float projectionWidthPixels,
        CaliperEdgePolarity firstPolarity,
        CaliperEdgePolarity secondPolarity,
        CaliperMeasurementSpec caliperSpec,
        int sampleCount = 12,
        float nominalRadiusOrSpanPixels = 40f,
        float ransacInlierThresholdPixels = DefaultRansacInlierThresholdPixels)
    {
        localRoiOffset.EnsureValid();
        caliperSpec.EnsureValid();
        if (!float.IsFinite(scanLengthPixels) || scanLengthPixels <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(scanLengthPixels), "scanLengthPixels must be positive and finite.");
        }

        if (!float.IsFinite(projectionWidthPixels) || projectionWidthPixels <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(projectionWidthPixels), "projectionWidthPixels must be positive and finite.");
        }

        if ((mode == CaliperMetroMode.RakeLineFit || mode == CaliperMetroMode.SpokeCircleFit) && sampleCount < MinimumSampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), $"sampleCount must be >= {MinimumSampleCount}.");
        }

        if (!float.IsFinite(nominalRadiusOrSpanPixels) || nominalRadiusOrSpanPixels <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalRadiusOrSpanPixels), "nominalRadiusOrSpanPixels must be positive and finite.");
        }

        if (!float.IsFinite(ransacInlierThresholdPixels) || ransacInlierThresholdPixels <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(ransacInlierThresholdPixels), "ransacInlierThresholdPixels must be positive and finite.");
        }

        Mode = mode;
        LocalRoiOffset = localRoiOffset;
        ScanLengthPixels = scanLengthPixels;
        ProjectionWidthPixels = projectionWidthPixels;
        FirstPolarity = firstPolarity;
        SecondPolarity = secondPolarity;
        CaliperSpec = caliperSpec;
        SampleCount = sampleCount;
        NominalRadiusOrSpanPixels = nominalRadiusOrSpanPixels;
        RansacInlierThresholdPixels = ransacInlierThresholdPixels;
        _isInitialized = true;
    }

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"{nameof(CaliperLineCircleToolSpec)} was not initialized via its constructor.");
        }
    }
}
