// File: src/VisionAlgorithm/OpenSrc/BlobRle/BinaryPixelCountSpec.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Configuration specification for <see cref="BinaryPixelCountInspector"/> and <c>BinaryPixelCountToolNode</c>
/// (Cognex <c>CogPixelCountTool</c> / Keyence Binary Window equivalent).
/// Evaluates foreground pixel count, area percentage, center of mass, and grayscale intensity statistics within a fixtured ROI.
/// </summary>
public readonly record struct BinaryPixelCountSpec
{
    private const float MaxPercent = 100.0f;
    private const int MinDimensionPixels = 2;

    private readonly bool _isInitialized;

    public FixturePose2D LocalRoiOffset { get; }
    public int RoiWidth { get; }
    public int RoiHeight { get; }
    public byte MinGrayThreshold { get; }
    public byte MaxGrayThreshold { get; }
    public float MinPassAreaPercent { get; }
    public float MaxPassAreaPercent { get; }
    public short ConditionIndex { get; }
    public int ConditionId { get; }
    public bool IsInitialized => _isInitialized && RoiWidth >= MinDimensionPixels && RoiHeight >= MinDimensionPixels;

    public BinaryPixelCountSpec(
        FixturePose2D localRoiOffset,
        int roiWidth,
        int roiHeight,
        byte minGrayThreshold = 128,
        byte maxGrayThreshold = byte.MaxValue,
        float minPassAreaPercent = 10.0f,
        float maxPassAreaPercent = MaxPercent,
        short conditionIndex = 0,
        int conditionId = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roiWidth, MinDimensionPixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(roiHeight, MinDimensionPixels);
        if (maxGrayThreshold < minGrayThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGrayThreshold), "maxGrayThreshold must be >= minGrayThreshold.");
        }

        LocalRoiOffset = localRoiOffset;
        RoiWidth = roiWidth;
        RoiHeight = roiHeight;
        MinGrayThreshold = minGrayThreshold;
        MaxGrayThreshold = maxGrayThreshold;
        MinPassAreaPercent = Math.Clamp(minPassAreaPercent, 0.0f, MaxPercent);
        MaxPassAreaPercent = Math.Clamp(MathF.Max(minPassAreaPercent, maxPassAreaPercent), 0.0f, MaxPercent);
        ConditionIndex = conditionIndex;
        ConditionId = conditionId;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException($"{nameof(BinaryPixelCountSpec)} was not initialized via its constructor.");
        }
    }
}
