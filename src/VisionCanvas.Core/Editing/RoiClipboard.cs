using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>In-app value snapshot; retains neither source images nor edit history.</summary>
public sealed class RoiClipboard
{
    public const double DefaultPasteOffsetDip = 20.0;

    private ImmutableArray<Roi> _entries = [];
    private BoundsD _bounds;
    private int _pastes;

    public bool HasContent => !_entries.IsEmpty;

    public long Count => _entries.Length;

    public long Copy(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SelectionCount == 0)
        {
            return 0;
        }

        var allSelected = state.Selection.Count == state.Rois.Count;
        var source = allSelected
            ? state.Rois.Values
            : RoiHierarchy.Subtree(state, RoiHierarchy.EffectiveRoots(state)).Select(id => state.Rois[id]);
        var copied = source.OrderBy(r => r.Order).ToImmutableArray();
        BoundsD combined = default;
        if (!copied.IsEmpty && allSelected && state.Snapshot.IsSpatialCreated)
        {
            combined = state.Snapshot.Spatial.Bounds;
        }
        else if (!copied.IsEmpty)
        {
            combined = copied[0].Geometry.Bounds;
            for (var i = 1; i < copied.Length; i++)
            {
                var next = copied[i].Geometry.Bounds;
                combined = new(
                    Math.Min(combined.Left, next.Left),
                    Math.Min(combined.Top, next.Top),
                    Math.Max(combined.Right, next.Right),
                    Math.Max(combined.Bottom, next.Bottom));
            }
        }

        _entries = copied;
        _bounds = combined;
        _pastes = 0;
        return Count;
    }

    /// <summary>Captures shared immutable entries and the next paste number without traversing any ROI.</summary>
    public RoiClipboardContent Capture() => new(_entries, _bounds, _pastes + 1);

    internal void Pasted() => _pastes++;
}
