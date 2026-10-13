using System.Collections.Frozen;
using System.Collections.Immutable;
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
    private readonly Lazy<RoiSpatialIndex> _repeatedConstraints;
    public ImmutableArray<RepeatedRoiBatch> Repeated
    {
        get;
    }
    private readonly Lazy<ImmutableArray<RepeatedRoiBatch>> _stationaryRepeated;
    public ImmutableArray<RepeatedRoiBatch> StationaryRepeated => _stationaryRepeated.Value;

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
        Affected = before.Selection.Count == before.Rois.Count || before.Snapshot.Children.Count == 0 ? before.Selection
            : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before)).ToFrozenSet();
        Repeated = before.RepeatedSelection.Select(pair => before.RepeatedRois[pair.Key].Slice(pair.Value))
            .OfType<RepeatedRoiBatch>().ToImmutableArray();
        _stationaryRepeated = new(() => before.RepeatedRois.Values.Select(batch => before.RepeatedSelection.TryGetValue(batch.Id, out var selected)
            ? batch.Slice(batch.Cells.Except(selected)) : batch).OfType<RepeatedRoiBatch>().ToImmutableArray());
        _repeatedConstraints = new(() => new(Repeated.SelectMany(batch => batch.Parts).SelectMany(part => part.ConstraintGeometries())
            .Select((geometry, index) => new Roi(new Guid(index, 0, 0, new byte[8]), "", geometry, null, false, index))));
        Bounds = Combine(before.SelectedSpatial.Bounds, before.Selection.Count != 0, _repeatedConstraints.Value.Bounds, !Repeated.IsEmpty);
        var all = Affected.Count == before.Rois.Count;
        _movingSearch = new(() => all ? before.Snapshot.SearchSpatial : before.Snapshot.SearchSpatial.Filter(Affected, include: true));
        _stationarySearch = new(() => all ? new([]) : before.Snapshot.SearchSpatial.Filter(Affected, include: false));
        _moving = new(() => all ? before.Snapshot.Spatial : ReferenceEquals(Affected, before.Selection) || Affected.SetEquals(before.Selection) ? before.SelectedSpatial
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

        if (Before.Image is not { } image || (Affected.Count == 0 && Repeated.IsEmpty))
        {
            return requested;
        }

        var bounds = Combine(Moving.Bounds, Affected.Count != 0, _repeatedConstraints.Value.Bounds, !Repeated.IsEmpty);
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
            cached = new(pivot, new(() =>
            {
                if (Before.Image is not { } image)
                {
                    return RotationRange.Unbounded;
                }

                var ordinary = Moving.RotationLimits(pivot, image);
                var repeated = _repeatedConstraints.Value.RotationLimits(pivot, image);
                return new RotationRange(Math.Max(ordinary.MinimumDegrees, repeated.MinimumDegrees), Math.Min(ordinary.MaximumDegrees, repeated.MaximumDegrees));
            }));
            Interlocked.Exchange(ref _rotationConstraint, cached);
        }
        return cached.Limits.Value;
    }

    public DocumentState Apply(RigidTransform2D transform, CancellationToken token = default)
    {
        if (transform.IsIdentity || (Affected.Count == 0 && Repeated.IsEmpty))
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
        var result = Affected.Count == Before.Rois.Count ? Before.WithRigidRois(rois.ToImmutable(), Affected, token)
            : Before.WithRois(rois.ToImmutable(), Affected);
        var batches = Before.RepeatedRois;
        foreach (var (id, cells) in Before.RepeatedSelection)
        {
            token.ThrowIfCancellationRequested();
            batches = batches.SetItem(id, batches[id].Transform(cells, transform));
        }
        return ReferenceEquals(batches, Before.RepeatedRois) ? result : result with
        {
            RepeatedRois = batches
        };
    }

    private static BoundsD Combine(BoundsD ordinary, bool hasOrdinary, BoundsD repeated, bool hasRepeated)
        => !hasOrdinary ? repeated : !hasRepeated ? ordinary : new(Math.Min(ordinary.Left, repeated.Left), Math.Min(ordinary.Top, repeated.Top),
            Math.Max(ordinary.Right, repeated.Right), Math.Max(ordinary.Bottom, repeated.Bottom));

    internal static RoiGeometry TransformGeometry(RoiGeometry geometry, RigidTransform2D transform) => transform.IsIdentity ? geometry
        : geometry.WithFrame(transform.Apply(geometry.Center), geometry.Width, geometry.Height, geometry.Angle + transform.AngleDegrees);

    private sealed record RotationConstraint(PointD Pivot, Lazy<RotationRange> Limits);
}

public sealed record RoiTransformPreview(RoiTransformSelection Selection, RigidTransform2D Transform)
{
    public RoiGeometry Geometry(Roi roi) => Selection.Affected.Contains(roi.Id) ? RoiTransformSelection.TransformGeometry(roi.Geometry, Transform) : roi.Geometry;
}
