using System.Collections.Frozen;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>Immutable selection and indexes reused by every sample of a rigid edit.</summary>
public sealed class RoiTransformSelection
{
    public DocumentState Before
    {
        get;
    }

    public IReadOnlySet<Guid> Affected
    {
        get;
    }

    private readonly Lazy<RoiSpatialIndex> _moving;

    private readonly Lazy<RoiSpatialIndex> _stationary;
    private readonly Lazy<RoiSpatialIndex> _movingSearch;
    private readonly Lazy<RoiSpatialIndex> _stationarySearch;
    private RotationConstraint? _rotationConstraint;

    public RoiSpatialIndex Moving => _moving.Value;

    public RoiSpatialIndex Stationary => _stationary.Value;

    public RoiSpatialIndex MovingSearch => _movingSearch.Value;

    public RoiSpatialIndex StationarySearch => _stationarySearch.Value;

    public BoundsD Bounds
    {
        get;
    }

    public bool IsPrepared
    {
        get
        {
            var cached = Volatile.Read(ref _rotationConstraint);
            return _moving.IsValueCreated && _stationary.IsValueCreated && _movingSearch.IsValueCreated && _stationarySearch.IsValueCreated && cached is { } constraint
                && constraint.Pivot == Bounds.Center && constraint.Limits.IsValueCreated;
        }
    }

    public RoiTransformSelection(DocumentState before)
    {
        Before = before;
        // Expanded membership never changes during a gesture; freeze once instead of building persistent tree nodes.
        Affected = before.Selection.Count == before.Rois.Count ? before.Selection
            : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before)).ToFrozenSet();
        Bounds = before.SelectedSpatial.Bounds;
        var all = Affected.Count == before.Rois.Count;
        _movingSearch = new(() => all ? before.Snapshot.SearchSpatial : before.Snapshot.SearchSpatial.Filter(Affected, include: true));
        _stationarySearch = new(() => all ? new([]) : before.Snapshot.SearchSpatial.Filter(Affected, include: false));
        _moving = new(() => all ? before.Snapshot.Spatial : Affected.SetEquals(before.Selection) ? before.SelectedSpatial
            : before.Snapshot.IsSpatialCreated && Affected.Count > 64 && Affected.Count > before.Rois.Count / 8 ? before.Snapshot.Spatial.Filter(Affected, include: true)
            : new(Affected.Select(id => before.Rois[id])));
        _stationary = new(() => all ? new([]) : Affected.Count <= 64 ? before.Snapshot.Spatial.WithChanges(Affected.Select(id => (id, (Roi?)null)))
            : before.Snapshot.IsSpatialCreated && before.Rois.Count - Affected.Count > before.Rois.Count / 8 ? before.Snapshot.Spatial.Filter(Affected, include: false)
            : new(before.Rois.Values.Where(r => !Affected.Contains(r.Id))));
    }

    public PointD Constrain(PointD requested)
    {
        if (!requested.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }

        if (Before.Image is not { } image || Affected.Count == 0)
        {
            return requested;
        }

        var bounds = Moving.Bounds;
        var fraction = 1d;
        if (requested.X > 0)
        {
            fraction = Math.Min(fraction, (image.Width - bounds.Right) / requested.X);
        }

        if (requested.X < 0)
        {
            fraction = Math.Min(fraction, -bounds.Left / requested.X);
        }

        if (requested.Y > 0)
        {
            fraction = Math.Min(fraction, (image.Height - bounds.Bottom) / requested.Y);
        }

        if (requested.Y < 0)
        {
            fraction = Math.Min(fraction, -bounds.Top / requested.Y);
        }

        return requested * Math.Clamp(fraction, 0, 1);
    }

    public RotationRange RotationLimits(PointD pivot)
    {
        if (!pivot.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(pivot));
        }

        var cached = Volatile.Read(ref _rotationConstraint);
        if (cached?.Pivot != pivot)
        {
            cached = new(pivot, new(() => Before.Image is { } image && Affected.Count != 0 ? Moving.RotationLimits(pivot, image) : RotationRange.Unbounded));
            Interlocked.Exchange(ref _rotationConstraint, cached);
        }
        return cached.Limits.Value;
    }

    public DocumentState Apply(RigidTransform2D transform, CancellationToken token = default)
    {
        if (transform.IsIdentity || Affected.Count == 0)
        {
            return Before;
        }

        var rois = Before.Rois.ToBuilder();
        foreach (var id in Affected)
        {
            token.ThrowIfCancellationRequested();
            var roi = rois[id];
            rois[id] = roi with
            {
                Geometry = TransformGeometry(roi.Geometry, transform)
            };
        }
        return Affected.Count == Before.Rois.Count ? Before.WithRigidRois(rois.ToImmutable(), Affected, token)
            : Before.WithRois(rois.ToImmutable(), Affected);
    }

    internal static RoiGeometry TransformGeometry(RoiGeometry geometry, RigidTransform2D transform) => transform.IsIdentity ? geometry
        : geometry.WithFrame(transform.Apply(geometry.Center), geometry.Width, geometry.Height, geometry.Angle + transform.AngleDegrees);

    private sealed record RotationConstraint(PointD Pivot, Lazy<RotationRange> Limits);
}

public sealed record RoiTransformPreview(RoiTransformSelection Selection, RigidTransform2D Transform)
{
    public RoiGeometry Geometry(Roi roi) => Selection.Affected.Contains(roi.Id) ? RoiTransformSelection.TransformGeometry(roi.Geometry, Transform) : roi.Geometry;
}
