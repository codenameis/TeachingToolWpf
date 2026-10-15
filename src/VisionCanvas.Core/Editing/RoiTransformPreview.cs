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
    private readonly Lazy<(RoiGeometry Parent, RoiGeometry Child)[]> _groupBoundaries;
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
            return _groupBoundaries.IsValueCreated && _moving.IsValueCreated && _stationary.IsValueCreated && _movingSearch.IsValueCreated && _stationarySearch.IsValueCreated && cached is { } constraint
                && constraint.Pivot == Bounds.Center && constraint.Limits.IsValueCreated;
        }
    }

    public RoiTransformSelection(DocumentState before)
    {
        Before = before;
        // Expanded membership never changes during a gesture; freeze once instead of building persistent tree nodes.
        Affected = before.Selection.Count == before.Rois.Count || before.Snapshot.Children.Count == 0 ? before.Selection
            : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before)).ToFrozenSet();
        _groupBoundaries = new(() => RoiGroupBounds.StationaryParents(before, Affected));
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
            : before.Snapshot.IsSpatialCreated ? before.Snapshot.Spatial.Filter(Affected, include: false)
            : new(before.Rois.Values.Where(r => !Affected.Contains(r.Id))));
    }

    public PointD Constrain(PointD requested, PointD previous = default, bool allowSlide = false)
    {
        if (!requested.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }

        if (Before.Image is not { } image || (Affected.Count == 0 && Repeated.IsEmpty))
        {
            return RoiGroupBounds.ConstrainTranslation(_groupBoundaries.Value, requested, previous, allowSlide);
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

        var imageBounds = new BoundsD(-bounds.Left, -bounds.Top, image.Width - bounds.Right, image.Height - bounds.Bottom);
        // 부모 벽을 따라 이동할 때 이미지 밖의 X 입력 때문에 유효한 Y 이동까지 축소하지 않는다.
        var target = allowSlide && _groupBoundaries.Value.Length > 0
            ? new PointD(Math.Clamp(requested.X, imageBounds.Left, imageBounds.Right), Math.Clamp(requested.Y, imageBounds.Top, imageBounds.Bottom))
            : requested * Math.Clamp(fraction, 0, 1);
        return RoiGroupBounds.ConstrainTranslation(_groupBoundaries.Value, target, previous, allowSlide, imageBounds);
    }

    public double ConstrainRotation(PointD pivot, double degrees)
        => RotationLimits(pivot).Clamp(degrees);

    public RotationRange RotationLimits(PointD pivot)
    {
        _ = _groupBoundaries.Value;
        if (!pivot.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(pivot));
        }

        var cached = Volatile.Read(ref _rotationConstraint);
        if (cached?.Pivot != pivot)
        {
            cached = new(pivot, new(() =>
            {
                var minimum = double.NegativeInfinity;
                var maximum = double.PositiveInfinity;
                if (Before.Image is { } image)
                {
                    var ordinary = Moving.RotationLimits(pivot, image);
                    var repeated = _repeatedConstraints.Value.RotationLimits(pivot, image);
                    minimum = Math.Max(ordinary.MinimumDegrees, repeated.MinimumDegrees);
                    maximum = Math.Min(ordinary.MaximumDegrees, repeated.MaximumDegrees);
                }
                foreach (var (parent, child) in _groupBoundaries.Value)
                {
                    var negative = RoiMotionBounds.RotationLimit(parent, child, pivot, -360);
                    var positive = RoiMotionBounds.RotationLimit(parent, child, pivot, 360);
                    minimum = Math.Max(minimum, negative <= -360 ? double.NegativeInfinity : negative);
                    maximum = Math.Min(maximum, positive >= 360 ? double.PositiveInfinity : positive);
                }
                return new RotationRange(minimum, maximum);
            }));
            Interlocked.Exchange(ref _rotationConstraint, cached);
        }
        return cached.Limits.Value;
    }

    public DocumentState Apply(RigidTransform2D transform, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ApplyCore(RoiGroupBounds.Constrain(_groupBoundaries.Value, transform), token);
    }

    internal DocumentState ApplyPreview(RigidTransform2D transform, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // 미리보기는 각 포인터 구간을 이미 검사했다. 시작점의 직선으로 다시 제한하면 우회 이동이 되튄다.
        if (_groupBoundaries.Value.Any(pair => !pair.Parent.Covers(TransformGeometry(pair.Child, transform))))
        {
            throw new InvalidOperationException("ROI 미리보기가 상위 그룹 경계를 벗어났습니다.");
        }
        return ApplyCore(transform, token);
    }

    private DocumentState ApplyCore(RigidTransform2D transform, CancellationToken token)
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
        // 큰 단일 그룹은 소수의 고정 조상만 제외한 같은 공간 이웃을 함께 옮긴다. 전체 정렬 대신 경계만 갱신한다.
        var refit = Affected.Count == Before.Rois.Count ||
            (Before.Selection.Count == 1 && Affected.Count > 4096 && Affected.Count > Before.Rois.Count / 2);
        var result = refit ? Before.WithRigidRois(rois.ToImmutable(), Affected, token)
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
