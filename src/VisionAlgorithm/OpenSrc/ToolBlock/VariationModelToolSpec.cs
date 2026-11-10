// File: src/VisionAlgorithm/OpenSrc/ToolBlock/VariationModelToolSpec.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Immutable configuration specification for <see cref="VariationModelToolNode"/> defining the fixture-relative ROI offset
/// and minimum defect pixel threshold.
/// </summary>
public readonly record struct VariationModelToolSpec
{
    private readonly bool _isInitialized;

    public FixturePose2D LocalRoiOffset { get; }
    public int MinimumDefectAreaPixels { get; }

    public bool IsValid => _isInitialized && LocalRoiOffset.IsValid && MinimumDefectAreaPixels >= 1;

    public VariationModelToolSpec(FixturePose2D localRoiOffset, int minimumDefectAreaPixels = 10)
    {
        localRoiOffset.EnsureValid();
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumDefectAreaPixels, 1);

        LocalRoiOffset = localRoiOffset;
        MinimumDefectAreaPixels = minimumDefectAreaPixels;
        _isInitialized = true;
    }

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"{nameof(VariationModelToolSpec)} was not initialized via its constructor.");
        }
    }
}
