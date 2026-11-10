// File: src/VisionAlgorithm/OpenSrc/ToolBlock/LargestBlobToolSpec.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Immutable configuration specification for <see cref="LargestBlobToolNode"/> defining the fixture-relative ROI,
/// grayscale threshold range, optional morphological RLE cleanup, and minimum blob area.
/// </summary>
public readonly record struct LargestBlobToolSpec
{
    private readonly bool _isInitialized;

    /// <summary>Gets the local ROI offset pose relative to the incoming <c>InFixture</c> pose.</summary>
    public FixturePose2D LocalRoiOffset { get; }

    /// <summary>Gets the rectified ROI width in pixels.</summary>
    public int RoiWidth { get; }

    /// <summary>Gets the rectified ROI height in pixels.</summary>
    public int RoiHeight { get; }

    /// <summary>Gets the inclusive minimum foreground grayscale intensity $[0, 255]$.</summary>
    public byte MinIntensity { get; }

    /// <summary>Gets the inclusive maximum foreground grayscale intensity $[0, 255]$.</summary>
    public byte MaxIntensity { get; }

    /// <summary>Gets the minimum connected-component area in pixels required to qualify as a valid blob.</summary>
    public long MinimumAreaPixels { get; }

    /// <summary>Gets the optional RLE morphological operation applied before connected-component analysis.</summary>
    public BlobMorphologyOperation MorphologyOperation { get; }

    /// <summary>Gets the horizontal half-width radius for the morphological box structuring element.</summary>
    public int MorphologyRadiusX { get; }

    /// <summary>Gets the vertical half-height radius for the morphological box structuring element.</summary>
    public int MorphologyRadiusY { get; }

    public bool IsValid => _isInitialized && LocalRoiOffset.IsValid && RoiWidth > 0 && RoiHeight > 0 && MinimumAreaPixels >= 1;

    public LargestBlobToolSpec(
        FixturePose2D localRoiOffset,
        int roiWidth,
        int roiHeight,
        byte minIntensity,
        byte maxIntensity = byte.MaxValue,
        long minimumAreaPixels = 1,
        BlobMorphologyOperation morphologyOperation = BlobMorphologyOperation.None,
        int morphologyRadiusX = 0,
        int morphologyRadiusY = 0)
    {
        localRoiOffset.EnsureValid();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiHeight, 0);
        if (maxIntensity < minIntensity)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIntensity), "maxIntensity must be >= minIntensity.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAreaPixels, 1L);
        ArgumentOutOfRangeException.ThrowIfNegative(morphologyRadiusX);
        ArgumentOutOfRangeException.ThrowIfNegative(morphologyRadiusY);

        LocalRoiOffset = localRoiOffset;
        RoiWidth = roiWidth;
        RoiHeight = roiHeight;
        MinIntensity = minIntensity;
        MaxIntensity = maxIntensity;
        MinimumAreaPixels = minimumAreaPixels;
        MorphologyOperation = morphologyOperation;
        MorphologyRadiusX = morphologyRadiusX;
        MorphologyRadiusY = morphologyRadiusY;
        _isInitialized = true;
    }

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"{nameof(LargestBlobToolSpec)} was not initialized via its constructor.");
        }
    }
}
