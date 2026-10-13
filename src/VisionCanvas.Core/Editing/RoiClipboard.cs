using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>In-app value snapshot; retains neither source images nor edit history.</summary>
public sealed class RoiClipboard
{
    private ImmutableArray<Roi> _entries = [];
    private ImmutableArray<RepeatedRoiBatch> _repeated = [];
    private BoundsD _bounds;
    private int _pastes;

    public bool HasContent => !_entries.IsEmpty || !_repeated.IsEmpty;

    public long Count => _entries.Length + _repeated.Sum(batch => (long)batch.Count);

    public long Copy(DocumentState state)
    {
        if (state.SelectionCount == 0)
        {
            return 0;
        }

        var allSelected = state.Selection.Count == state.Rois.Count;
        var source = allSelected ? state.Rois.Values
            : RoiHierarchy.Subtree(state, RoiHierarchy.EffectiveRoots(state)).Select(id => state.Rois[id]);
        var copied = source.OrderBy(r => r.Order).ToImmutableArray();
        var repeated = state.RepeatedSelection.Select(pair => state.RepeatedRois[pair.Key].Slice(pair.Value))
            .OfType<RepeatedRoiBatch>().OrderBy(batch => batch.Order).ToImmutableArray();
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
                combined = new(Math.Min(combined.Left, next.Left), Math.Min(combined.Top, next.Top),
                    Math.Max(combined.Right, next.Right), Math.Max(combined.Bottom, next.Bottom));
            }
        }
        var hasBounds = !copied.IsEmpty;
        foreach (var batch in repeated)
        {
            var next = batch.Bounds;
            combined = hasBounds ? new(Math.Min(combined.Left, next.Left), Math.Min(combined.Top, next.Top),
                Math.Max(combined.Right, next.Right), Math.Max(combined.Bottom, next.Bottom)) : next;
            hasBounds = true;
        }
        _entries = copied;
        _repeated = repeated;
        _bounds = combined;
        _pastes = 0;
        return Count;
    }

    /// <summary>Captures shared immutable entries and the next paste number without traversing any ROI.</summary>
    public RoiClipboardContent Capture() => new(_entries, _repeated, _bounds, _pastes + 1);

    internal void Pasted() => _pastes++;
}

/// <summary>Worker-owned placement input, independent of later copy or paste actions.</summary>
public sealed class RoiClipboardContent
{
    public ImmutableArray<RepeatedRoiBatch> Repeated
    {
        get;
    }
    internal long OrderCount => Entries.Length + Repeated.Length;
    public ImmutableArray<Roi> Entries
    {
        get;
    }

    public BoundsD Bounds
    {
        get;
    }

    /// <summary>One-based sequence number of the next paste at capture time.</summary>
    public int PasteNumber
    {
        get;
    }

    internal RoiClipboardContent(ImmutableArray<Roi> entries, ImmutableArray<RepeatedRoiBatch> repeated, BoundsD bounds, int pasteNumber)
    {
        Entries = entries;
        Repeated = repeated;
        Bounds = bounds;
        PasteNumber = pasteNumber;
    }

    public ImmutableArray<Roi> Place(ImageReference image, PointD step, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Entries.IsEmpty)
        {
            return [];
        }

        var placement = GetPlacement(image, step);
        var placed = ImmutableArray.CreateBuilder<Roi>(Entries.Length);
        foreach (var roi in Entries)
        {
            token.ThrowIfCancellationRequested();
            placed.Add(roi with
            {
                Geometry = placement.Apply(roi.Geometry)
            });
        }
        token.ThrowIfCancellationRequested();
        return placed.MoveToImmutable();
    }

    internal Placement GetPlacement(ImageReference image, PointD step)
    {
        var scale = Math.Min(1, Math.Min(image.Width / Bounds.Width, image.Height / Bounds.Height));
        var halfWidth = Bounds.Width * scale / 2;
        var halfHeight = Bounds.Height * scale / 2;
        var desired = Bounds.Center + (step * PasteNumber);
        var center = new PointD(Math.Clamp(desired.X, halfWidth, image.Width - halfWidth),
            Math.Clamp(desired.Y, halfHeight, image.Height - halfHeight));
        return new(Bounds.Center, center, scale);
    }

    internal readonly record struct Placement(PointD SourceCenter, PointD TargetCenter, double Scale)
    {
        public RoiGeometry Apply(RoiGeometry geometry)
            => geometry.WithFrame(TargetCenter + ((geometry.Center - SourceCenter) * Scale),
                geometry.Width * Scale, geometry.Height * Scale, geometry.Angle);
    }
}
