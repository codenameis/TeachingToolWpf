using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Imaging.Filters;

public enum RoiFilterKind { None, Binarize, Edge }
public sealed record FilterSettings(RoiFilterKind Kind, double Threshold = 128, double EdgeLow = 50, double EdgeHigh = 150)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) ||
            (Kind == RoiFilterKind.Binarize && (!double.IsFinite(Threshold) || Threshold is < 0 or > 255)) ||
            (Kind == RoiFilterKind.Edge && (!double.IsFinite(EdgeLow) || !double.IsFinite(EdgeHigh) || EdgeLow < 0 || EdgeHigh > 255 || EdgeLow >= EdgeHigh)))
            throw new ArgumentException("임계값은 0–255, Edge 하한은 상한보다 작아야 합니다.");
    }
}
public sealed record FilterRequest(ImageReference Image, bool IsLive, IReadOnlyList<Roi> Rois, FilterSettings Settings);
/// <summary>Immutable by ownership: bytes are published once and never mutated after construction.</summary>
public sealed record FilteredRoi(Guid Id, RotatedRect Geometry, int Width, int Height, byte[] Gray8);
public sealed record FilterPreviewResult(IReadOnlyList<FilteredRoi> Regions, double Milliseconds);

/// <summary>One caller at a time; no UI/GPU access, no source edits, cancellation discards incomplete results.</summary>
public interface IRoiFilterPreview
{
    Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken cancellationToken);
}
