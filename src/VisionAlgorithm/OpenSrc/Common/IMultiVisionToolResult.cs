using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Universal contract for multi-instance vision inspection results (such as multi-barcode 1D/2D readers,
/// multi-object pattern matchers, or multi-particle analyzers) where a single ROI or frame yields <c>0..N</c>
/// individual <typeparamref name="TItem"/> detections, each carrying its own world pose, verdict, and replay geometry.
/// </summary>
public interface IMultiVisionToolResult<TItem> : IVisionToolResult
    where TItem : IVisionToolResult
{
    /// <summary>Gets the total number of detected items.</summary>
    int Count { get; }

    /// <summary>Gets the number of detected items whose individual verdict passed.</summary>
    int PassCount { get; }

    /// <summary>Gets the read-only contiguous buffer of individual <typeparamref name="TItem"/> results in reading order.</summary>
    ReadOnlyMemory<TItem> Items { get; }

    /// <summary>
    /// Exports one full <see cref="VisionInspectionEnvelope"/> per detected item so downstream ToolBlock / SPC /
    /// <c>VisionCanvas</c> replay consumers preserve every individual pose, payload, and pass/fail verdict without loss.
    /// </summary>
    VisionInspectionEnvelope[] ToInspectionEnvelopes(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN);
}
